namespace DistSear.Abstractions.Search;

public sealed record FacetBucket(string Key, long Count);

/// <summary>
/// A merged aggregation. Terms facets computed across shards are inherently approximate: a shard
/// only reports its own top buckets, so a term ranked just below the cutoff on every shard can be
/// undercounted. The error fields quantify that rather than hiding it.
/// </summary>
public sealed record FacetResult
{
    public required string Name { get; init; }

    public required IReadOnlyList<FacetBucket> Buckets { get; init; }

    /// <summary>
    /// Maximum count that could be missing from any returned bucket, summed over shards that did
    /// not report the term. Zero means the counts are exact.
    /// </summary>
    public long DocCountErrorUpperBound { get; init; }

    /// <summary>Total count of documents in buckets that fell outside the returned set.</summary>
    public long SumOtherDocCount { get; init; }
}
