using DistSear.Abstractions.Cluster;
using DistSear.Abstractions.Search;
using DistSear.Abstractions.Transport;
using DistSear.Analysis;
using DistSear.Index;
using DistSear.Index.Facets;
using DistSear.Index.Highlight;
using DistSear.Index.Query;
using DistSear.Index.Scoring;
using DistSear.Index.Suggest;

// The query AST root shares its name with its namespace, so alias it for readability here.
using SearchQuery = DistSear.Index.Query.Query;

namespace DistSear.Node.Shards;

/// <summary>
/// Executes the shard-local half of a distributed query.
///
/// The query phase deliberately returns identity and ranking keys only. Shipping document bodies
/// here would mean every shard sends its full top-k across the network, most of which loses the
/// merge and is discarded. The fetch phase then pulls bodies for the winners alone.
/// </summary>
public sealed class ShardQueryService
{
    private readonly ShardHost _host;
    private readonly AnalyzerRegistry _analyzers;
    private readonly Highlighter _highlighter;

    public ShardQueryService(ShardHost host, AnalyzerRegistry analyzers)
    {
        _host = host;
        _analyzers = analyzers;
        _highlighter = new Highlighter(analyzers);
    }

    public ShardQueryResult Query(
        ShardQueryRequest request,
        IReadOnlyCollection<string>? principals,
        CancellationToken cancellationToken = default)
    {
        var shard = Require(request.Index, request.ShardId);
        var search = request.Search;

        var query = BuildQuery(shard, search);
        var context = BuildContext(shard, request.GlobalStatistics);

        var facets = BuildFacetCollectors(search.Facets);

        var outcome = shard.Index.Searcher.Search(
            new SearchExecution
            {
                Query = query,
                Context = context,

                // The coordinator merges, so each shard must offer its whole top (from + size);
                // trimming to `size` here would lose documents that place globally.
                From = 0,
                Size = search.TopDocsNeeded,
                Sort = search.Sort,
                SearchAfter = search.SearchAfter,
                Facets = facets,
                Principals = principals
            },
            cancellationToken);

        return new ShardQueryResult
        {
            ShardId = request.ShardId,
            TotalHits = outcome.TopDocs.TotalHits,
            MaxScore = outcome.TopDocs.MaxScore,
            Hits =
            [
                .. outcome.TopDocs.Hits.Select(h => new ShardDocRef
                {
                    Id = h.ExternalId,
                    Score = h.Score,
                    SortValues = h.SortValues
                })
            ],
            Facets = outcome.Facets
        };
    }

    public ShardFetchResult Fetch(ShardFetchRequest request, CancellationToken cancellationToken = default)
    {
        var shard = Require(request.Index, request.ShardId);
        var mapping = shard.Mapping;

        IReadOnlyList<IHighlightMatcher> matchers = [];

        if (request.Highlight is not null && !string.IsNullOrWhiteSpace(request.Query))
        {
            var parser = new QueryParser(mapping, _analyzers);
            matchers = HighlightTermExtractor.Extract(parser.Parse(request.Query));
        }

        var hits = new List<SearchHit>(request.Ids.Count);

        foreach (var id in request.Ids)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var stored = shard.Index.GetStoredFields(id);

            if (stored is null)
            {
                continue;
            }

            var fields = request.Fields is null
                ? stored
                : stored
                    .Where(kv => request.Fields.Contains(kv.Key))
                    .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

            hits.Add(new SearchHit
            {
                Id = id,
                Score = 0,
                ShardId = request.ShardId,
                Fields = fields,
                Highlights = BuildHighlights(shard, stored, matchers, request.Highlight)
            });
        }

        return new ShardFetchResult { ShardId = request.ShardId, Hits = hits };
    }

    /// <summary>
    /// This shard's view of the corpus, for the coordinator's global-statistics pre-pass. Only the
    /// terms the query touches are reported, keeping the payload proportional to the query.
    /// </summary>
    public CollectionStatistics Statistics(ShardStatsRequest request)
    {
        var shard = Require(request.Index, request.ShardId);
        var statistics = shard.Index.LocalStatistics();

        var terms = request.TermKeys
            .Select(SplitTermKey)
            .Where(t => t is not null)
            .Select(t => t!.Value);

        return statistics.Export(terms);
    }

    public SuggestResponse Suggest(
        int shardId,
        SuggestRequest request,
        IReadOnlyCollection<string>? principals,
        CancellationToken cancellationToken = default)
    {
        var shard = Require(request.Index, shardId);
        return new Suggester(shard.Index).Suggest(request, principals, cancellationToken);
    }

    /// <summary>Terms this query will look up, so the coordinator knows what statistics to gather.</summary>
    public IReadOnlyList<string> CollectTermKeys(string index, int shardId, SearchRequest search)
    {
        var shard = Require(index, shardId);
        var query = BuildQuery(shard, search);
        var weight = query.CreateWeight(BuildContext(shard, null), 1.0);

        var terms = new HashSet<(string Field, string Term)>();
        weight.CollectTerms(terms);

        return [.. terms.Select(t => CollectionStatistics.TermKey(t.Field, t.Term))];
    }

    private SearchQuery BuildQuery(ShardRuntime shard, SearchRequest search)
    {
        var parser = new QueryParser(shard.Mapping, _analyzers);
        var query = parser.Parse(search.Query ?? string.Empty);

        if (string.IsNullOrWhiteSpace(search.Filter))
        {
            return query;
        }

        // A filter restricts membership without contributing to the score, which is why it is a
        // separate clause rather than being folded into the query text.
        return new BooleanQuery(
        [
            new BooleanClause(query, Occur.Must),
            new BooleanClause(parser.Parse(search.Filter), Occur.Filter)
        ]);
    }

    private static SearchContext BuildContext(ShardRuntime shard, CollectionStatistics? global)
    {
        var local = shard.Index.LocalStatistics();

        return new SearchContext
        {
            Mapping = shard.Mapping,
            Analyzers = shard.Index.Analyzers,
            Statistics = global is null ? local : new GlobalTermStatistics(global, local)
        };
    }

    private static List<IFacetCollector> BuildFacetCollectors(IReadOnlyList<FacetSpec> specs) =>
    [
        .. specs.Select(IFacetCollector (spec) => spec.Kind == FacetKind.Range
            ? new RangeFacetCollector(spec)
            : new TermsFacetCollector(spec))
    ];

    private IReadOnlyDictionary<string, IReadOnlyList<string>> BuildHighlights(
        ShardRuntime shard,
        IReadOnlyDictionary<string, object?> stored,
        IReadOnlyList<IHighlightMatcher> matchers,
        HighlightSpec? spec)
    {
        if (spec is null || matchers.Count == 0)
        {
            return new Dictionary<string, IReadOnlyList<string>>();
        }

        var highlights = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        foreach (var fieldName in spec.Fields)
        {
            var mapping = shard.Mapping.Get(fieldName);

            if (mapping is null || !stored.TryGetValue(fieldName, out var raw) || raw is not string text)
            {
                continue;
            }

            var passages = _highlighter.Highlight(mapping, text, matchers, spec);

            if (passages.Count > 0)
            {
                highlights[fieldName] = passages;
            }
        }

        return highlights;
    }

    private ShardRuntime Require(string index, int shardId) =>
        _host.Find(index, shardId)
        ?? throw new ShardNotFoundException(index, shardId, _host.NodeId);

    /// <summary>Reverses the length-prefixed composite key used on the wire.</summary>
    private static (string Field, string Term)? SplitTermKey(string key)
    {
        var separator = key.IndexOf(':', StringComparison.Ordinal);

        if (separator <= 0 || !int.TryParse(key.AsSpan(0, separator), out var fieldLength))
        {
            return null;
        }

        var start = separator + 1;

        if (start + fieldLength > key.Length)
        {
            return null;
        }

        return (key.Substring(start, fieldLength), key[(start + fieldLength)..]);
    }
}

/// <summary>
/// Raised when a node is asked for a shard it does not hold, which happens naturally while an
/// allocation change propagates. The coordinator treats it as a shard failure and, where a replica
/// exists, retries there.
/// </summary>
public sealed class ShardNotFoundException : Exception
{
    public ShardNotFoundException(string index, int shardId, string nodeId)
        : base($"Node '{nodeId}' does not hold shard {shardId} of index '{index}'.")
    {
        Index = index;
        ShardId = shardId;
        NodeId = nodeId;
    }

    public string Index { get; }

    public int ShardId { get; }

    public string NodeId { get; }
}
