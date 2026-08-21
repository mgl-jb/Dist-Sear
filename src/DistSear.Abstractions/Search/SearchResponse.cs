namespace DistSear.Abstractions.Search;

public sealed record ShardFailure(int ShardId, string Reason);

/// <summary>
/// Per-query shard accounting. A query over a partially unavailable cluster returns the results it
/// could gather and reports the shortfall here instead of failing outright.
/// </summary>
public sealed record ShardStatistics
{
    public required int Total { get; init; }

    public required int Successful { get; init; }

    public int Skipped { get; init; }

    public int Failed { get; init; }

    public IReadOnlyList<ShardFailure> Failures { get; init; } = [];

    public bool IsPartial => Failed > 0 || Successful < Total - Skipped;
}

public sealed record SearchHit
{
    public required string Id { get; init; }

    public required double Score { get; init; }

    public int ShardId { get; init; }

    public IReadOnlyDictionary<string, object?> Fields { get; init; } =
        new Dictionary<string, object?>();

    /// <summary>Highlighted passages per field, in descending passage-score order.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Highlights { get; init; } =
        new Dictionary<string, IReadOnlyList<string>>();

    /// <summary>Sort key values for this hit; feed the last hit's values back as <c>SearchAfter</c>.</summary>
    public IReadOnlyList<object?> SortValues { get; init; } = [];

    public string? Explanation { get; init; }
}

public sealed record SearchResponse
{
    public required IReadOnlyList<SearchHit> Hits { get; init; }

    public required long TotalHits { get; init; }

    /// <summary>
    /// True when the total was early-terminated and is therefore a lower bound rather than exact.
    /// </summary>
    public bool TotalIsLowerBound { get; init; }

    public double MaxScore { get; init; }

    public IReadOnlyDictionary<string, FacetResult> Facets { get; init; } =
        new Dictionary<string, FacetResult>();

    public required ShardStatistics Shards { get; init; }

    public bool TimedOut { get; init; }

    public long TookMilliseconds { get; init; }
}
