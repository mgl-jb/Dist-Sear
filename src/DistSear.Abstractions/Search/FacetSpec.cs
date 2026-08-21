namespace DistSear.Abstractions.Search;

public enum FacetKind
{
    Terms,
    Range
}

public sealed record FacetRange(string Key, double? From, double? To);

/// <summary>
/// Requests an aggregation over a doc-values field.
/// </summary>
public sealed record FacetSpec
{
    public required string Name { get; init; }

    public required string Field { get; init; }

    public FacetKind Kind { get; init; } = FacetKind.Terms;

    /// <summary>Number of buckets to return to the caller.</summary>
    public int Size { get; init; } = 10;

    /// <summary>
    /// Buckets each shard returns. Larger than <see cref="Size"/> because a term that is globally
    /// in the top-N may be outside any single shard's top-N. Defaults to <c>Size * 1.5 + 10</c>.
    /// </summary>
    public int? ShardSize { get; init; }

    public IReadOnlyList<FacetRange> Ranges { get; init; } = [];

    public int EffectiveShardSize => ShardSize ?? (int)(Size * 1.5) + 10;
}
