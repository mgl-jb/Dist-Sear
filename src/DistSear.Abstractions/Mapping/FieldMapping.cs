namespace DistSear.Abstractions.Mapping;

/// <summary>
/// Declares how a single document field is analyzed, stored and queried.
/// </summary>
public sealed record FieldMapping
{
    public required string Name { get; init; }

    public required FieldType Type { get; init; }

    /// <summary>
    /// Name of the analyzer applied at index time. Only meaningful for <see cref="FieldType.Text"/>.
    /// </summary>
    public string Analyzer { get; init; } = "standard";

    /// <summary>
    /// Name of the analyzer applied to query text targeting this field. Defaults to <see cref="Analyzer"/>.
    /// Index- and search-time analyzers must differ for synonym-expansion and edge-n-gram fields,
    /// otherwise the expansion is applied twice.
    /// </summary>
    public string? SearchAnalyzer { get; init; }

    /// <summary>Whether the raw value is retained so it can be returned in hits and highlighted.</summary>
    public bool Stored { get; init; } = true;

    /// <summary>Whether a column-stride doc-value is written, enabling sorting and faceting.</summary>
    public bool DocValues { get; init; }

    /// <summary>Whether term positions are recorded, enabling phrase and proximity queries.</summary>
    public bool Positions { get; init; } = true;

    /// <summary>Static per-field boost folded into the BM25 score at query time.</summary>
    public double Boost { get; init; } = 1.0;

    /// <summary>Whether this field feeds the completion suggester.</summary>
    public bool Suggest { get; init; }

    public string EffectiveSearchAnalyzer => SearchAnalyzer ?? Analyzer;
}
