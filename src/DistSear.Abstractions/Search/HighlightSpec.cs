namespace DistSear.Abstractions.Search;

public sealed record HighlightSpec
{
    /// <summary>Fields to highlight. Each must be stored.</summary>
    public required IReadOnlyList<string> Fields { get; init; }

    public string PreTag { get; init; } = "<em>";

    public string PostTag { get; init; } = "</em>";

    /// <summary>Approximate character length of each returned passage.</summary>
    public int FragmentSize { get; init; } = 160;

    /// <summary>Maximum passages returned per field, best-scoring first.</summary>
    public int MaxFragments { get; init; } = 3;
}
