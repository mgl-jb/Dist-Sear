using System.Diagnostics;
using DistSear.Abstractions.Cluster;
using DistSear.Abstractions.Mapping;
using DistSear.Abstractions.Search;
using DistSear.Abstractions.Storage;
using DistSear.Abstractions.Transport;
using DistSear.Analysis;
using DistSear.Cluster.Allocation;
using DistSear.Index.Facets;
using DistSear.Index.Query;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DistSear.Coordinator.Search;

/// <summary>Who is asking. Drives document-level filtering on every shard.</summary>
public sealed record SearchPrincipal(IReadOnlyList<string> Groups)
{
    /// <summary>Unrestricted access, for internal calls only.</summary>
    public static SearchPrincipal Unrestricted { get; } = new([]) { EnforceAcl = false };

    public bool EnforceAcl { get; init; } = true;

    public IReadOnlyList<string>? Principals => EnforceAcl ? Groups : null;
}

public sealed class IndexNotFoundException(string index)
    : Exception($"Index or alias '{index}' does not exist.");

/// <summary>
/// Executes a distributed search across every shard of an index.
///
/// The algorithm is <c>query_then_fetch</c>. Phase one asks every searchable shard for its own top
/// <c>from + size</c>, carrying identity and ranking keys only; the coordinator merges those into a
/// global top-k. Phase two fetches document bodies and highlights from just the shards that own the
/// winners. The point is that a shard whose hits all lose the merge never sends a single document
/// body across the network.
/// </summary>
public sealed class SearchCoordinator
{
    /// <summary>Traces the fan-out, so a slow shard is visible as a span rather than inferred.</summary>
    public static readonly ActivitySource ActivitySource = new("DistSear.Coordinator");

    private readonly IClusterStore _clusterStore;
    private readonly INodeTransport _transport;
    private readonly AdaptiveReplicaSelector _replicas;
    private readonly AnalyzerRegistry _analyzers;
    private readonly CoordinatorOptions _options;
    private readonly ILogger<SearchCoordinator>? _logger;

    public SearchCoordinator(
        IClusterStore clusterStore,
        INodeTransport transport,
        AdaptiveReplicaSelector replicas,
        AnalyzerRegistry analyzers,
        IOptions<CoordinatorOptions> options,
        ILogger<SearchCoordinator>? logger = null)
    {
        _clusterStore = clusterStore;
        _transport = transport;
        _replicas = replicas;
        _analyzers = analyzers;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<SearchResponse> SearchAsync(
        SearchRequest request,
        SearchPrincipal caller,
        CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();

        using var activity = ActivitySource.StartActivity("search.coordinator");
        activity?.SetTag("search.index", request.Index);

        ValidatePage(request);

        var plan = await PlanAsync(request.Index, cancellationToken);
        var effective = request with { Index = plan.IndexName };

        var globalStatistics = effective.SearchType == SearchType.DfsQueryThenFetch
            ? await GatherGlobalStatisticsAsync(plan, effective, cancellationToken)
            : null;

        var (results, failures) = await QueryPhaseAsync(plan, effective, caller, globalStatistics, cancellationToken);

        var merged = ShardResultMerger.Merge(results, effective.Sort, effective.From, effective.Size);
        var hits = await FetchPhaseAsync(plan, effective, merged, caller, cancellationToken);

        var statistics = new ShardStatistics
        {
            Total = plan.Shards.Count + failures.Count,
            Successful = results.Count,
            Failed = failures.Count,
            Failures = failures
        };

        activity?.SetTag("search.shards.failed", failures.Count);

        return new SearchResponse
        {
            Hits = hits,
            TotalHits = results.Sum(r => r.TotalHits),
            TotalIsLowerBound = failures.Count > 0,
            MaxScore = results.Count == 0 ? 0 : results.Max(r => r.MaxScore),
            Facets = MergeFacets(effective.Facets, results),
            Shards = statistics,
            TimedOut = failures.Count > 0,
            TookMilliseconds = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds
        };
    }

    private void ValidatePage(SearchRequest request)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(request.From);
        ArgumentOutOfRangeException.ThrowIfNegative(request.Size);

        if (request.TopDocsNeeded > _options.MaxPageSize)
        {
            // Every shard must produce from+size candidates for the merge to be correct, so deep
            // offset paging multiplies work across the cluster. The cursor exists for that case.
            throw new ArgumentOutOfRangeException(
                nameof(request),
                $"from + size is {request.TopDocsNeeded}, above the limit of {_options.MaxPageSize}. "
                + "Use search_after to page beyond this depth.");
        }
    }

    /// <summary>Resolves the target index and picks a replica for each of its shards.</summary>
    private async Task<QueryPlan> PlanAsync(string nameOrAlias, CancellationToken cancellationToken)
    {
        var state = await _clusterStore.GetAsync(cancellationToken);
        var indexName = state.ResolveIndex(nameOrAlias);

        if (!state.Indexes.TryGetValue(indexName, out var metadata))
        {
            throw new IndexNotFoundException(nameOrAlias);
        }

        var targets = new List<ShardTarget>();
        var failures = new List<ShardFailure>();

        foreach (var allocation in state.ShardsOf(indexName).OrderBy(s => s.ShardId))
        {
            var candidates = allocation.Searchable.Select(c => c.NodeId).ToList();

            if (candidates.Count == 0)
            {
                failures.Add(new ShardFailure(allocation.ShardId, "No copy of this shard is available."));
                continue;
            }

            targets.Add(new ShardTarget(allocation.ShardId, candidates));
        }

        return new QueryPlan(indexName, metadata.Mapping, targets, failures);
    }

    /// <summary>
    /// Sums document frequencies across shards so BM25 uses global IDF.
    ///
    /// Without this each shard scores from its own statistics, and two identical documents can rank
    /// differently purely because of how the corpus happened to divide. The cost is one extra round
    /// trip, which is why it is opt-in per request.
    /// </summary>
    private async Task<CollectionStatistics?> GatherGlobalStatisticsAsync(
        QueryPlan plan,
        SearchRequest request,
        CancellationToken cancellationToken)
    {
        using var activity = ActivitySource.StartActivity("search.statistics");

        var parser = new QueryParser(plan.Mapping, _analyzers);
        var parsed = parser.Parse(request.Query ?? string.Empty);

        var termKeys = QueryTerms.Collect(parsed)
            .Select(t => CollectionStatistics.TermKey(t.Field, t.Term))
            .ToList();

        if (termKeys.Count == 0)
        {
            return null;
        }

        var tasks = plan.Shards.Select(async target =>
        {
            var node = _replicas.Select(target.Candidates);

            if (node is null)
            {
                return null;
            }

            try
            {
                return await _transport.StatisticsAsync(
                    node,
                    new ShardStatsRequest
                    {
                        Index = plan.IndexName,
                        ShardId = target.ShardId,
                        TermKeys = termKeys
                    },
                    cancellationToken);
            }
            catch (Exception exception)
            {
                // A shard that cannot report statistics simply does not contribute to the global
                // view; the query still runs, slightly less exactly.
                _logger?.LogWarning(exception, "Shard {Shard} did not report statistics.", target.ShardId);
                return null;
            }
        });

        var parts = (await Task.WhenAll(tasks)).OfType<CollectionStatistics>().ToList();

        return parts.Count == 0 ? null : CollectionStatistics.Merge(parts);
    }

    private async Task<(List<ShardQueryResult> Results, List<ShardFailure> Failures)> QueryPhaseAsync(
        QueryPlan plan,
        SearchRequest request,
        SearchPrincipal caller,
        CollectionStatistics? globalStatistics,
        CancellationToken cancellationToken)
    {
        using var activity = ActivitySource.StartActivity("search.query-phase");
        activity?.SetTag("search.shards.total", plan.Shards.Count);

        var timeout = request.Timeout ?? _options.ShardTimeout;

        var tasks = plan.Shards.Select(target => WithReplicaFailoverAsync(
            target,
            timeout,
            (node, token) => _transport.QueryAsync(
                node,
                new ShardQueryRequest
                {
                    Index = plan.IndexName,
                    ShardId = target.ShardId,
                    Search = request,
                    GlobalStatistics = globalStatistics
                },
                token),
            cancellationToken));

        var outcomes = await Task.WhenAll(tasks);

        var results = new List<ShardQueryResult>(plan.Shards.Count);
        var failures = new List<ShardFailure>(plan.Failures);

        foreach (var outcome in outcomes)
        {
            if (outcome.Value is not null)
            {
                results.Add(outcome.Value);
            }
            else
            {
                failures.Add(new ShardFailure(outcome.ShardId, outcome.Error ?? "Unknown failure."));
            }
        }

        return (results, failures);
    }

    private async Task<IReadOnlyList<SearchHit>> FetchPhaseAsync(
        QueryPlan plan,
        SearchRequest request,
        IReadOnlyList<MergedHit> merged,
        SearchPrincipal caller,
        CancellationToken cancellationToken)
    {
        if (merged.Count == 0)
        {
            return [];
        }

        using var activity = ActivitySource.StartActivity("search.fetch-phase");

        // Only the shards that actually own a winning document are contacted.
        var byShard = merged
            .GroupBy(h => h.ShardId)
            .ToDictionary(g => g.Key, g => g.Select(h => h.Hit.Id).ToList());

        activity?.SetTag("search.shards.fetched", byShard.Count);

        var timeout = request.Timeout ?? _options.ShardTimeout;
        var targets = plan.Shards.Where(t => byShard.ContainsKey(t.ShardId)).ToList();

        var tasks = targets.Select(target => WithReplicaFailoverAsync(
            target,
            timeout,
            (node, token) => _transport.FetchAsync(
                node,
                new ShardFetchRequest
                {
                    Index = plan.IndexName,
                    ShardId = target.ShardId,
                    Ids = byShard[target.ShardId],
                    Fields = request.Fields,
                    Highlight = request.Highlight,
                    Query = request.Query
                },
                token),
            cancellationToken));

        var fetched = await Task.WhenAll(tasks);

        var bodies = fetched
            .Where(o => o.Value is not null)
            .SelectMany(o => o.Value!.Hits)
            .ToDictionary(h => h.Id, StringComparer.Ordinal);

        var hits = new List<SearchHit>(merged.Count);

        foreach (var hit in merged)
        {
            // Ranking comes from the query phase; the fetch only supplies the body. A document
            // whose fetch failed is still returned, with its score, rather than silently dropped.
            bodies.TryGetValue(hit.Hit.Id, out var body);

            hits.Add(new SearchHit
            {
                Id = hit.Hit.Id,
                Score = hit.Hit.Score,
                ShardId = hit.ShardId,
                Fields = body?.Fields ?? new Dictionary<string, object?>(),
                Highlights = body?.Highlights ?? new Dictionary<string, IReadOnlyList<string>>(),
                SortValues = hit.Hit.SortValues,
                Explanation = hit.Hit.Explanation
            });
        }

        return hits;
    }

    /// <summary>
    /// Runs one shard call, trying another replica if the first fails or misses its deadline.
    ///
    /// Failures are contained here rather than propagated: a query over a partly unavailable
    /// cluster should return what it can and report the shortfall, because most of a result set is
    /// far more useful than an error.
    /// </summary>
    private async Task<ShardOutcome<T>> WithReplicaFailoverAsync<T>(
        ShardTarget target,
        TimeSpan timeout,
        Func<string, CancellationToken, Task<T>> call,
        CancellationToken cancellationToken)
        where T : class
    {
        var attempted = new HashSet<string>(StringComparer.Ordinal);
        string? lastError = null;

        for (var attempt = 0; attempt < _options.MaxReplicaAttempts; attempt++)
        {
            var candidates = target.Candidates.Where(c => !attempted.Contains(c)).ToList();
            var node = _replicas.Select(candidates);

            if (node is null)
            {
                break;
            }

            attempted.Add(node);

            using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timer.CancelAfter(timeout);

            using var scope = _replicas.BeginRequest(node);

            try
            {
                return new ShardOutcome<T>(target.ShardId, await call(node, timer.Token), null);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The caller gave up; stop rather than retrying into a cancelled request.
                throw;
            }
            catch (OperationCanceledException)
            {
                lastError = $"Shard {target.ShardId} on node '{node}' exceeded {timeout.TotalMilliseconds:F0} ms.";
                _replicas.RecordFailure(node);
            }
            catch (Exception exception)
            {
                lastError = $"Shard {target.ShardId} on node '{node}' failed: {exception.Message}";
                _replicas.RecordFailure(node);
            }
        }

        _logger?.LogWarning("Shard {Shard} failed on every replica: {Error}", target.ShardId, lastError);

        return new ShardOutcome<T>(target.ShardId, null, lastError ?? "No replica available.");
    }

    private static IReadOnlyDictionary<string, FacetResult> MergeFacets(
        IReadOnlyList<FacetSpec> specs,
        IReadOnlyList<ShardQueryResult> results)
    {
        if (specs.Count == 0 || results.Count == 0)
        {
            return new Dictionary<string, FacetResult>();
        }

        var merged = new Dictionary<string, FacetResult>(StringComparer.Ordinal);

        foreach (var spec in specs)
        {
            var parts = results
                .Select(r => r.Facets.GetValueOrDefault(spec.Name))
                .OfType<FacetResult>()
                .ToList();

            if (parts.Count > 0)
            {
                merged[spec.Name] = FacetMerger.Merge(spec.Name, parts, spec.Size);
            }
        }

        return merged;
    }

    private sealed record ShardTarget(int ShardId, IReadOnlyList<string> Candidates);

    private sealed record ShardOutcome<T>(int ShardId, T? Value, string? Error) where T : class;

    private sealed record QueryPlan(
        string IndexName,
        IndexMapping Mapping,
        IReadOnlyList<ShardTarget> Shards,
        IReadOnlyList<ShardFailure> Failures);
}
