using DistSear.Abstractions.Search;
using DistSear.Index.Collectors;
using DistSear.Index.Facets;
using DistSear.Index.Query;
using DistSear.Index.Segments;

namespace DistSear.Index;

/// <summary>Everything one shard-local search needs.</summary>
public sealed record SearchExecution
{
    public required Query.Query Query { get; init; }

    public required SearchContext Context { get; init; }

    public int From { get; init; }

    public int Size { get; init; } = 10;

    public IReadOnlyList<SortSpec> Sort { get; init; } = [];

    public IReadOnlyList<object?>? SearchAfter { get; init; }

    public IReadOnlyList<IFacetCollector> Facets { get; init; } = [];

    /// <summary>
    /// Identities the caller holds. Null disables document-level security entirely, which is only
    /// correct for internal calls; the coordinator always supplies the caller's principals.
    /// </summary>
    public IReadOnlyCollection<string>? Principals { get; init; }

    /// <summary>
    /// Matches counted exactly before top-k pruning may begin. Raise it to guarantee an exact
    /// total for a larger result set, at the cost of scoring more documents.
    /// </summary>
    public long TotalHitsThreshold { get; init; } = TopDocsCollector.DefaultTotalHitsThreshold;
}

public sealed record SearchOutcome(TopDocs TopDocs, IReadOnlyDictionary<string, FacetResult> Facets);

/// <summary>
/// Runs a query across the segments of one shard.
///
/// The segment list is a point-in-time snapshot: segments are immutable and deletions are read from
/// a copied bitset, so a concurrent refresh cannot change the result set underneath a running query.
/// </summary>
public sealed class IndexSearcher
{
    /// <summary>Documents between cancellation checks. Frequent enough to stay responsive, rare
    /// enough that the check does not show up in the inner loop.</summary>
    private const int CancellationCheckInterval = 1024;

    private readonly IReadOnlyList<Segment> _segments;
    private readonly LiveDocs[] _liveDocs;
    private readonly int[] _docBases;

    public IndexSearcher(IReadOnlyList<Segment> segments)
    {
        _segments = segments;
        _liveDocs = new LiveDocs[segments.Count];
        _docBases = new int[segments.Count];

        var docBase = 0;

        for (var i = 0; i < segments.Count; i++)
        {
            _docBases[i] = docBase;
            _liveDocs[i] = segments[i].LiveDocs.Snapshot();
            docBase += segments[i].MaxDoc;
        }

        MaxDoc = docBase;
    }

    public int MaxDoc { get; }

    public int LiveDocCount => _liveDocs.Sum(l => l.LiveCount);

    public IReadOnlyList<Segment> Segments => _segments;

    public SearchOutcome Search(SearchExecution execution, CancellationToken cancellationToken = default)
    {
        var weight = execution.Query.CreateWeight(execution.Context, 1.0);
        var capacity = execution.From + execution.Size;
        var collector = new TopDocsCollector(
            capacity,
            execution.Sort,
            execution.SearchAfter,
            execution.TotalHitsThreshold);

        var principals = execution.Principals is null
            ? null
            : new HashSet<string>(execution.Principals, StringComparer.Ordinal);

        for (var ordinal = 0; ordinal < _segments.Count; ordinal++)
        {
            var segment = _segments[ordinal];

            if (segment.MaxDoc == 0)
            {
                continue;
            }

            collector.SetSegment(segment, ordinal, _docBases[ordinal]);

            foreach (var facet in execution.Facets)
            {
                facet.SetSegment(segment);
            }

            var scorer = weight.CreateScorer(segment);

            if (scorer is null)
            {
                continue;
            }

            CollectSegment(scorer, segment, _liveDocs[ordinal], collector, execution, principals, cancellationToken);
        }

        var facets = execution.Facets.ToDictionary(f => f.Name, f => f.Build(), StringComparer.Ordinal);

        return new SearchOutcome(collector.GetTopDocs(execution.From), facets);
    }

    private static void CollectSegment(
        Scorer scorer,
        Segment segment,
        LiveDocs liveDocs,
        TopDocsCollector collector,
        SearchExecution execution,
        HashSet<string>? principals,
        CancellationToken cancellationToken)
    {
        var acls = segment.Acls;
        var seen = 0;
        int docId;

        while ((docId = scorer.NextDoc()) != Scorer.NoMoreDocs)
        {
            if (++seen % CancellationCheckInterval == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (!liveDocs.IsLive(docId))
            {
                continue;
            }

            if (principals is not null && !IsVisible(acls[docId], principals))
            {
                continue;
            }

            var score = scorer.Score();

            collector.Collect(docId, score);

            foreach (var facet in execution.Facets)
            {
                facet.Collect(docId);
            }

            // Feeding the threshold back lets WAND skip documents that can no longer place.
            scorer.SetMinCompetitiveScore(collector.MinCompetitiveScore);
        }
    }

    /// <summary>
    /// Document-level security. Enforced here rather than as an ordinary query clause so that no
    /// caller-supplied query can rewrite, negate or omit it.
    /// </summary>
    private static bool IsVisible(string[] acl, HashSet<string> principals)
    {
        // An empty ACL means the document carries no restriction.
        if (acl.Length == 0)
        {
            return true;
        }

        foreach (var entry in acl)
        {
            if (principals.Contains(entry))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Counts matches without ranking them, for <c>_count</c> requests.</summary>
    public long Count(
        Query.Query query,
        SearchContext context,
        IReadOnlyCollection<string>? principals = null,
        CancellationToken cancellationToken = default)
    {
        var execution = new SearchExecution
        {
            Query = query,
            Context = context,
            From = 0,
            Size = 0,
            Principals = principals,

            // A count must be exact, so never let pruning skip a match.
            TotalHitsThreshold = long.MaxValue
        };

        return Search(execution, cancellationToken).TopDocs.TotalHits;
    }
}
