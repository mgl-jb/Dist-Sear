using DistSear.Abstractions.Mapping;
using DistSear.Index.Segments;

namespace DistSear.Index.Query;

/// <summary>
/// Numeric, date and boolean range filter, answered from doc values rather than from the inverted
/// index.
///
/// The inverted index cannot answer a range without enumerating every term in it, which is why
/// engines that do this well build a separate numeric structure (a BKD tree, say). Here the
/// column-stride doc values already hold one comparable double per document, so a range becomes a
/// linear scan of that column. That is <c>O(maxDoc)</c> rather than <c>O(matching terms)</c> — a
/// deliberate trade: ranges are almost always filters, combined with a selective scoring clause
/// that drives iteration, and the scan is a tight loop over a contiguous array.
/// </summary>
public sealed class NumericRangeQuery : Query
{
    public NumericRangeQuery(
        string field,
        double? lower,
        double? upper,
        bool includeLower = true,
        bool includeUpper = true)
    {
        Field = field;
        Lower = lower;
        Upper = upper;
        IncludeLower = includeLower;
        IncludeUpper = includeUpper;
    }

    public string Field { get; }

    public double? Lower { get; }

    public double? Upper { get; }

    public bool IncludeLower { get; }

    public bool IncludeUpper { get; }

    /// <summary>Builds a range over whatever the mapping says the field's type is.</summary>
    public static NumericRangeQuery Create(
        FieldMapping mapping,
        object? lower,
        object? upper,
        bool includeLower = true,
        bool includeUpper = true) =>
        new(
            mapping.Name,
            lower is null ? null : FieldCoercion.ToSortableDouble(lower, mapping.Type),
            upper is null ? null : FieldCoercion.ToSortableDouble(upper, mapping.Type),
            includeLower,
            includeUpper);

    public override Weight CreateWeight(SearchContext context, double boost) =>
        new RangeWeight(this, boost);

    public override string Describe()
    {
        var open = IncludeLower ? '[' : '{';
        var close = IncludeUpper ? ']' : '}';
        var lower = Lower?.ToString("R") ?? "*";
        var upper = Upper?.ToString("R") ?? "*";

        return $"{Field}:{open}{lower} TO {upper}{close}";
    }

    private sealed class RangeWeight : Weight
    {
        private readonly double _boost;

        public RangeWeight(NumericRangeQuery query, double boost)
            : base(query) => _boost = boost;

        private new NumericRangeQuery Query => (NumericRangeQuery)base.Query;

        public override Scorer? CreateScorer(Segment segment)
        {
            if (segment.GetDocValues(Query.Field) is not NumericDocValues values)
            {
                // Without doc values the range cannot be evaluated. Mapping validation catches this
                // at index-creation time, so reaching here means the field genuinely has no values
                // in this segment.
                return null;
            }

            return new RangeScorer(values, segment.MaxDoc, Query, _boost);
        }
    }

    private sealed class RangeScorer : Scorer
    {
        private readonly NumericDocValues _values;
        private readonly int _maxDoc;
        private readonly NumericRangeQuery _query;
        private readonly double _boost;
        private int _docId = -1;

        public RangeScorer(NumericDocValues values, int maxDoc, NumericRangeQuery query, double boost)
        {
            _values = values;
            _maxDoc = maxDoc;
            _query = query;
            _boost = boost;
        }

        public override int DocId => _docId;

        public override long Cost => _maxDoc;

        public override double MaxScore => _boost;

        public override int NextDoc() => Advance(_docId + 1);

        public override int Advance(int target)
        {
            for (var doc = Math.Max(target, 0); doc < _maxDoc; doc++)
            {
                if (Contains(doc))
                {
                    return _docId = doc;
                }
            }

            return _docId = NoMoreDocs;
        }

        private bool Contains(int doc)
        {
            if (!_values.HasValue(doc))
            {
                return false;
            }

            var value = _values.GetDouble(doc);

            if (_query.Lower is { } lower && (_query.IncludeLower ? value < lower : value <= lower))
            {
                return false;
            }

            if (_query.Upper is { } upper && (_query.IncludeUpper ? value > upper : value >= upper))
            {
                return false;
            }

            return true;
        }

        public override double Score() => _boost;
    }
}

/// <summary>
/// Lexicographic range over a keyword field, resolved against the sorted term dictionary. Unlike
/// the numeric case this seeks directly to the lower bound and stops at the upper, so its cost is
/// proportional to the terms actually in range.
/// </summary>
public sealed class TermRangeQuery : MultiTermQuery
{
    public TermRangeQuery(
        string field,
        string? lower,
        string? upper,
        bool includeLower = true,
        bool includeUpper = true)
        : base(field, MultiTermScoreMode.ConstantScore)
    {
        Lower = lower;
        Upper = upper;
        IncludeLower = includeLower;
        IncludeUpper = includeUpper;
    }

    public string? Lower { get; }

    public string? Upper { get; }

    public bool IncludeLower { get; }

    public bool IncludeUpper { get; }

    protected internal override IEnumerable<(string Term, double Weight)> Expand(
        FieldTerms field,
        int maxExpansions)
    {
        var terms = field.SortedTerms;
        var start = Lower is null ? 0 : field.SeekTo(Lower);
        var produced = 0;

        for (var i = start; i < terms.Length && produced < maxExpansions; i++)
        {
            var term = terms[i];

            if (!IncludeLower && Lower is not null && string.Equals(term, Lower, StringComparison.Ordinal))
            {
                continue;
            }

            if (Upper is not null)
            {
                var comparison = string.CompareOrdinal(term, Upper);

                if (comparison > 0 || (comparison == 0 && !IncludeUpper))
                {
                    yield break;
                }
            }

            produced++;
            yield return (term, 1.0);
        }
    }

    public override string Describe()
    {
        var open = IncludeLower ? '[' : '{';
        var close = IncludeUpper ? ']' : '}';

        return $"{Field}:{open}{Lower ?? "*"} TO {Upper ?? "*"}{close}";
    }
}
