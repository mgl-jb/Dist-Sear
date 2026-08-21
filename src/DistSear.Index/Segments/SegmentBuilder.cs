using DistSear.Abstractions.Documents;
using DistSear.Abstractions.Mapping;
using DistSear.Analysis;
using DistSear.Index.Postings;

namespace DistSear.Index.Segments;

/// <summary>
/// Accumulates analyzed documents and seals them into an immutable <see cref="Segment"/>.
/// Documents are assigned sequential internal ids, which is what allows postings to be appended in
/// increasing document order and therefore gap-encoded as they are built.
/// </summary>
public sealed class SegmentBuilder
{
    private readonly IndexMapping _mapping;
    private readonly AnalyzerRegistry _analyzers;

    private readonly Dictionary<string, FieldAccumulator> _fields = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NumericAccumulator> _numericDocValues = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string[]>> _keywordDocValues = new(StringComparer.Ordinal);

    private readonly List<string> _ids = [];
    private readonly List<Dictionary<string, object?>> _stored = [];
    private readonly List<string[]> _acls = [];

    public SegmentBuilder(IndexMapping mapping, AnalyzerRegistry analyzers)
    {
        _mapping = mapping;
        _analyzers = analyzers;
    }

    public int DocumentCount => _ids.Count;

    /// <summary>Rough heap cost, used to decide when the in-memory segment should be flushed.</summary>
    public long EstimatedBytes { get; private set; }

    /// <summary>Adds a document and returns the internal id it was assigned.</summary>
    public int AddDocument(IndexedDocument document)
    {
        var docId = _ids.Count;

        _ids.Add(document.Id);
        _acls.Add([.. document.Acl]);

        var stored = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (var field in _mapping.Fields)
        {
            if (!document.Fields.TryGetValue(field.Name, out var raw) || raw is null)
            {
                continue;
            }

            if (field.Stored)
            {
                stored[field.Name] = raw;
            }

            var values = FieldCoercion.Flatten(raw).ToList();

            if (values.Count == 0)
            {
                continue;
            }

            IndexField(docId, field, values);
            WriteDocValues(docId, field, values);
        }

        _stored.Add(stored);
        return docId;
    }

    private void IndexField(int docId, FieldMapping field, List<object> values)
    {
        var accumulator = GetFieldAccumulator(field);
        var termPositions = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var length = 0;

        if (field.Type == FieldType.Text)
        {
            var analyzer = _analyzers.ForIndexing(field);
            var positionBase = 0;

            foreach (var value in values)
            {
                var text = value as string ?? value.ToString() ?? string.Empty;
                var highest = -1;

                foreach (var token in analyzer.Analyze(text))
                {
                    var position = positionBase + token.Position;
                    Append(termPositions, token.Term, position);
                    highest = Math.Max(highest, token.Position);
                    length++;
                }

                // Leave a gap between values so a phrase cannot straddle two separate values.
                positionBase += highest + 1 + FieldCoercion.PositionIncrementGap;
            }
        }
        else
        {
            var position = 0;

            foreach (var value in values)
            {
                Append(termPositions, FieldCoercion.ToTerm(value, field.Type), position++);
                length++;
            }
        }

        if (length == 0)
        {
            return;
        }

        PadTo(accumulator.Norms, docId);
        accumulator.Norms.Add(length);
        accumulator.SumFieldLength += length;
        accumulator.DocumentCount++;

        foreach (var (term, positions) in termPositions)
        {
            if (!accumulator.Terms.TryGetValue(term, out var postings))
            {
                postings = new PostingsListBuilder(field.Positions);
                accumulator.Terms[term] = postings;
                EstimatedBytes += term.Length * 2 + 64;
            }

            postings.Add(docId, positions.Count, field.Positions ? positions : null);
            EstimatedBytes += 4 + (field.Positions ? positions.Count * 2 : 0);
        }
    }

    private static void Append(Dictionary<string, List<int>> map, string term, int position)
    {
        if (!map.TryGetValue(term, out var positions))
        {
            positions = [];
            map[term] = positions;
        }

        // Synonyms and n-grams can emit the same term twice at one position; keep the list
        // non-decreasing so gap encoding stays valid.
        if (positions.Count == 0 || positions[^1] <= position)
        {
            positions.Add(position);
        }
    }

    private void WriteDocValues(int docId, FieldMapping field, List<object> values)
    {
        if (!field.DocValues)
        {
            return;
        }

        if (FieldCoercion.IsNumeric(field.Type))
        {
            if (!_numericDocValues.TryGetValue(field.Name, out var numeric))
            {
                numeric = new NumericAccumulator();
                _numericDocValues[field.Name] = numeric;
            }

            PadTo(numeric.Values, docId);
            PadTo(numeric.Present, docId);
            numeric.Values.Add(FieldCoercion.ToSortableDouble(values[0], field.Type));
            numeric.Present.Add(true);
        }
        else
        {
            if (!_keywordDocValues.TryGetValue(field.Name, out var keyword))
            {
                keyword = [];
                _keywordDocValues[field.Name] = keyword;
            }

            PadTo(keyword, docId);
            keyword.Add([.. values.Select(v => FieldCoercion.ToTerm(v, field.Type))]);
        }
    }

    private FieldAccumulator GetFieldAccumulator(FieldMapping field)
    {
        if (!_fields.TryGetValue(field.Name, out var accumulator))
        {
            accumulator = new FieldAccumulator();
            _fields[field.Name] = accumulator;
        }

        return accumulator;
    }

    /// <summary>Documents that lack a field leave a hole; pad it so list index equals document id.</summary>
    private static void PadTo<T>(List<T> list, int docId)
    {
        while (list.Count < docId)
        {
            list.Add(default!);
        }
    }

    public Segment Build()
    {
        var maxDoc = _ids.Count;

        var fields = new Dictionary<string, FieldTerms>(StringComparer.Ordinal);

        foreach (var (name, accumulator) in _fields)
        {
            PadTo(accumulator.Norms, maxDoc);

            var terms = accumulator.Terms.ToDictionary(
                kv => kv.Key,
                kv => kv.Value.Build(),
                StringComparer.Ordinal);

            fields[name] = new FieldTerms(
                terms,
                [.. accumulator.Norms],
                accumulator.SumFieldLength,
                accumulator.DocumentCount);
        }

        var docValues = new Dictionary<string, DocValuesColumn>(StringComparer.Ordinal);

        foreach (var (name, numeric) in _numericDocValues)
        {
            PadTo(numeric.Values, maxDoc);
            PadTo(numeric.Present, maxDoc);
            docValues[name] = new NumericDocValues([.. numeric.Values], [.. numeric.Present]);
        }

        foreach (var (name, keyword) in _keywordDocValues)
        {
            PadTo(keyword, maxDoc);
            docValues[name] = new KeywordDocValues([.. keyword.Select(v => v ?? [])]);
        }

        return new Segment(
            [.. _ids],
            fields,
            docValues,
            [.. _stored],
            [.. _acls]);
    }

    private sealed class FieldAccumulator
    {
        public Dictionary<string, PostingsListBuilder> Terms { get; } = new(StringComparer.Ordinal);

        public List<int> Norms { get; } = [];

        public long SumFieldLength { get; set; }

        public int DocumentCount { get; set; }
    }

    private sealed class NumericAccumulator
    {
        public List<double> Values { get; } = [];

        public List<bool> Present { get; } = [];
    }
}
