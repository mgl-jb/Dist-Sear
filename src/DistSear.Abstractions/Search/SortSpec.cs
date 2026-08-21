namespace DistSear.Abstractions.Search;

/// <summary>A single sort key. Sorting by a field requires that field to have doc values.</summary>
public sealed record SortSpec(string Field, bool Descending = false)
{
    /// <summary>Pseudo-field naming the relevance score.</summary>
    public const string ScoreField = "_score";

    /// <summary>Pseudo-field naming the document id, used as the final tiebreaker.</summary>
    public const string DocIdField = "_id";

    public bool IsScore => string.Equals(Field, ScoreField, StringComparison.Ordinal);

    public static SortSpec ByScore { get; } = new(ScoreField, Descending: true);
}
