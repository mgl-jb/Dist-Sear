using DistSear.Abstractions.Cluster;
using DistSear.Abstractions.Search;

namespace DistSear.Abstractions.Transport;

/// <summary>A shard-local hit: identity and ranking keys only, no document body.</summary>
public sealed record ShardDocRef
{
    public required string Id { get; init; }

    public required double Score { get; init; }

    public IReadOnlyList<object?> SortValues { get; init; } = [];

    public string? Explanation { get; init; }
}

public sealed record ShardQueryRequest
{
    public required string Index { get; init; }

    public required int ShardId { get; init; }

    public required SearchRequest Search { get; init; }

    /// <summary>
    /// Global corpus statistics gathered by the DFS pre-pass. When null the shard scores with its
    /// own local statistics.
    /// </summary>
    public CollectionStatistics? GlobalStatistics { get; init; }
}

public sealed record ShardQueryResult
{
    public required int ShardId { get; init; }

    public required IReadOnlyList<ShardDocRef> Hits { get; init; }

    public long TotalHits { get; init; }

    public double MaxScore { get; init; }

    /// <summary>Per-shard facet counts, merged by the coordinator.</summary>
    public IReadOnlyDictionary<string, FacetResult> Facets { get; init; } =
        new Dictionary<string, FacetResult>();
}

/// <summary>Phase two: pull bodies and highlights for the documents that survived the merge.</summary>
public sealed record ShardFetchRequest
{
    public required string Index { get; init; }

    public required int ShardId { get; init; }

    public required IReadOnlyList<string> Ids { get; init; }

    public IReadOnlyList<string>? Fields { get; init; }

    public HighlightSpec? Highlight { get; init; }

    /// <summary>Original query text, needed to locate the terms a highlighter should mark up.</summary>
    public string? Query { get; init; }
}

public sealed record ShardFetchResult
{
    public required int ShardId { get; init; }

    public required IReadOnlyList<SearchHit> Hits { get; init; }
}

public sealed record ShardStatsRequest
{
    public required string Index { get; init; }

    public required int ShardId { get; init; }

    /// <summary>Terms whose document frequency the coordinator needs, as (field, term) key pairs.</summary>
    public required IReadOnlyList<string> TermKeys { get; init; }
}

/// <summary>
/// Calls from the coordinator to an index node. The HTTP implementation talks to Container Apps
/// internal ingress; an in-process implementation makes the whole cluster testable without a network.
/// </summary>
public interface INodeTransport
{
    Task<ShardQueryResult> QueryAsync(
        string nodeId,
        ShardQueryRequest request,
        CancellationToken cancellationToken);

    Task<ShardFetchResult> FetchAsync(
        string nodeId,
        ShardFetchRequest request,
        CancellationToken cancellationToken);

    Task<CollectionStatistics> StatisticsAsync(
        string nodeId,
        ShardStatsRequest request,
        CancellationToken cancellationToken);

    Task<SuggestResponse> SuggestAsync(
        string nodeId,
        int shardId,
        SuggestRequest request,
        CancellationToken cancellationToken);
}
