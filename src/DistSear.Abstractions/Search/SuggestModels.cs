namespace DistSear.Abstractions.Search;

public sealed record SuggestRequest
{
    public required string Index { get; init; }

    public required string Text { get; init; }

    /// <summary>Field to complete against. Must be mapped with <c>Suggest = true</c>.</summary>
    public required string Field { get; init; }

    public int Size { get; init; } = 5;

    /// <summary>
    /// When true, and prefix completion yields nothing, fall back to did-you-mean correction over
    /// the term dictionary.
    /// </summary>
    public bool AllowCorrection { get; init; } = true;
}

public sealed record Suggestion(string Text, double Score, long Frequency);

public sealed record SuggestResponse
{
    public required IReadOnlyList<Suggestion> Suggestions { get; init; }

    /// <summary>Set when results came from spell correction rather than prefix completion.</summary>
    public bool Corrected { get; init; }

    public required ShardStatistics Shards { get; init; }
}
