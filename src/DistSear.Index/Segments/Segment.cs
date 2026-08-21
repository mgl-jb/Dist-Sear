using DistSear.Index.Postings;

namespace DistSear.Index.Segments;

/// <summary>
/// The inverted index for one field within one segment, plus the per-document length statistics
/// BM25 needs to normalise for field length.
/// </summary>
public sealed class FieldTerms
{
    private readonly Dictionary<string, PostingsList> _terms;
    private string[]? _sortedTerms;

    internal FieldTerms(
        Dictionary<string, PostingsList> terms,
        int[] norms,
        long sumFieldLength,
        int documentCount)
    {
        _terms = terms;
        Norms = norms;
        SumFieldLength = sumFieldLength;
        DocumentCount = documentCount;
    }

    /// <summary>Length in terms of this field per document; zero where the document lacks the field.</summary>
    public int[] Norms { get; }

    public long SumFieldLength { get; }

    /// <summary>Documents that actually carry this field. Denominator for the mean length.</summary>
    public int DocumentCount { get; }

    public double AverageFieldLength => DocumentCount > 0 ? (double)SumFieldLength / DocumentCount : 0;

    public int TermCount => _terms.Count;

    public IEnumerable<string> Terms => _terms.Keys;

    public PostingsList? GetPostings(string term) => _terms.GetValueOrDefault(term);

    public int DocumentFrequency(string term) => _terms.TryGetValue(term, out var p) ? p.DocumentFrequency : 0;

    /// <summary>
    /// The term dictionary in sorted order, built once on demand. Sorting is what lets prefix,
    /// range and fuzzy queries seek to a starting point and stop early, instead of scanning every
    /// term in the field.
    /// </summary>
    public string[] SortedTerms => _sortedTerms ??= [.. _terms.Keys.Order(StringComparer.Ordinal)];

    /// <summary>Index of the first term at or after <paramref name="target"/> in sorted order.</summary>
    public int SeekTo(string target)
    {
        var index = Array.BinarySearch(SortedTerms, target, StringComparer.Ordinal);
        return index >= 0 ? index : ~index;
    }

    /// <summary>Terms with the given prefix. Backs prefix, wildcard and completion queries.</summary>
    public IEnumerable<string> TermsWithPrefix(string prefix)
    {
        if (prefix.Length == 0)
        {
            return SortedTerms;
        }

        return EnumeratePrefix(prefix);
    }

    private IEnumerable<string> EnumeratePrefix(string prefix)
    {
        var terms = SortedTerms;

        for (var i = SeekTo(prefix); i < terms.Length; i++)
        {
            if (!terms[i].StartsWith(prefix, StringComparison.Ordinal))
            {
                yield break;
            }

            yield return terms[i];
        }
    }

    public IEnumerable<KeyValuePair<string, PostingsList>> AllTerms => _terms;
}

/// <summary>
/// An immutable slice of the index. Once built, a segment is never modified: updates and deletes
/// are recorded in <see cref="LiveDocs"/>, and space is reclaimed only by merging. Immutability is
/// what lets a reader take a lock-free point-in-time snapshot of the index.
/// </summary>
public sealed class Segment
{
    internal Segment(
        string[] externalIds,
        Dictionary<string, FieldTerms> fields,
        Dictionary<string, DocValuesColumn> docValues,
        Dictionary<string, object?>[] storedFields,
        string[][] acls)
    {
        ExternalIds = externalIds;
        Fields = fields;
        DocValues = docValues;
        StoredFields = storedFields;
        Acls = acls;
        LiveDocs = new LiveDocs(externalIds.Length);
    }

    public int MaxDoc => ExternalIds.Length;

    /// <summary>Maps an internal, segment-local document id to the caller's document id.</summary>
    public string[] ExternalIds { get; }

    public IReadOnlyDictionary<string, FieldTerms> Fields { get; }

    public IReadOnlyDictionary<string, DocValuesColumn> DocValues { get; }

    public Dictionary<string, object?>[] StoredFields { get; }

    public string[][] Acls { get; }

    public LiveDocs LiveDocs { get; }

    public FieldTerms? GetField(string field) => Fields.GetValueOrDefault(field);

    public DocValuesColumn? GetDocValues(string field) => DocValues.GetValueOrDefault(field);

    public string GetExternalId(int docId) => ExternalIds[docId];
}
