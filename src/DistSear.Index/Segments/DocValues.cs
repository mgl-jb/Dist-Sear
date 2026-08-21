namespace DistSear.Index.Segments;

/// <summary>
/// Column-stride storage: values laid out per document rather than per term. Sorting and faceting
/// need to read a field's value for an arbitrary document, which the inverted index cannot answer
/// without scanning every term.
/// </summary>
public abstract class DocValuesColumn
{
    public abstract bool HasValue(int docId);

    /// <summary>All values for a document; empty when unset. Multi-valued fields yield several.</summary>
    public abstract IReadOnlyList<object> GetValues(int docId);

    /// <summary>
    /// Single value used for sorting. Multi-valued fields yield their first value, which keeps sort
    /// order deterministic without inventing an aggregate the caller did not ask for.
    /// </summary>
    public object? GetSortValue(int docId)
    {
        var values = GetValues(docId);
        return values.Count > 0 ? values[0] : null;
    }
}

/// <summary>Numeric doc values, held as doubles so a single column serves long, double and date fields.</summary>
public sealed class NumericDocValues : DocValuesColumn
{
    private readonly double[] _values;
    private readonly bool[] _present;

    public NumericDocValues(double[] values, bool[] present)
    {
        _values = values;
        _present = present;
    }

    /// <summary>Number of documents the column covers.</summary>
    public int Count => _values.Length;

    public override bool HasValue(int docId) => (uint)docId < (uint)_present.Length && _present[docId];

    public override IReadOnlyList<object> GetValues(int docId) =>
        HasValue(docId) ? [_values[docId]] : [];

    public double GetDouble(int docId, double missing = double.NaN) =>
        HasValue(docId) ? _values[docId] : missing;
}

/// <summary>Keyword doc values. Multi-valued, because a document can carry several tags or ACL entries.</summary>
public sealed class KeywordDocValues : DocValuesColumn
{
    private readonly string[][] _values;

    public KeywordDocValues(string[][] values) => _values = values;

    /// <summary>Number of documents the column covers.</summary>
    public int Count => _values.Length;

    public override bool HasValue(int docId) =>
        (uint)docId < (uint)_values.Length && _values[docId].Length > 0;

    public override IReadOnlyList<object> GetValues(int docId) =>
        (uint)docId < (uint)_values.Length ? _values[docId] : [];

    public IReadOnlyList<string> GetStrings(int docId) =>
        (uint)docId < (uint)_values.Length ? _values[docId] : [];
}
