namespace DistSear.Abstractions.Mapping;

/// <summary>
/// The logical type of a mapped field. The type drives analysis, doc-value storage
/// and which query clauses are legal against the field.
/// </summary>
public enum FieldType
{
    /// <summary>Full-text: analyzed into terms, scored with BM25, supports phrase queries.</summary>
    Text,

    /// <summary>Not analyzed. Indexed verbatim as a single term. Filterable, sortable, facetable.</summary>
    Keyword,

    /// <summary>64-bit signed integer. Range-queryable and sortable.</summary>
    Long,

    /// <summary>Double-precision float. Range-queryable and sortable.</summary>
    Double,

    /// <summary>Boolean. Indexed as the keyword "true"/"false".</summary>
    Boolean,

    /// <summary>Instant in time, indexed and sorted as epoch milliseconds.</summary>
    Date
}
