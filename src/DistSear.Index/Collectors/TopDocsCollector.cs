using System.Globalization;
using DistSear.Abstractions.Search;
using DistSear.Index.Segments;

namespace DistSear.Index.Collectors;

/// <summary>A hit retained by the collector, carrying everything needed to rank it globally.</summary>
public sealed record CollectedHit(
    int SegmentOrdinal,
    int DocId,
    int GlobalDocId,
    string ExternalId,
    double Score,
    object?[] SortValues);

public sealed record TopDocs(
    IReadOnlyList<CollectedHit> Hits,
    long TotalHits,
    double MaxScore,
    bool TotalIsLowerBound);

/// <summary>
/// Keeps the best <c>k</c> hits using a bounded min-heap, so memory is <c>O(k)</c> however many
/// documents match. The heap's weakest entry doubles as the score threshold handed back to WAND,
/// which is what lets the scorer skip documents that cannot displace it.
/// </summary>
public sealed class TopDocsCollector
{
    private readonly PriorityQueue<CollectedHit, CollectedHit> _heap;
    private readonly int _capacity;
    private readonly IReadOnlyList<SortSpec> _sort;
    private readonly object?[]? _searchAfter;
    private readonly bool _sortByScoreOnly;

    private DocValuesColumn?[] _sortColumns = [];
    private Segment? _segment;
    private int _segmentOrdinal;
    private int _docBase;

    private readonly long _totalHitsThreshold;

    public TopDocsCollector(
        int capacity,
        IReadOnlyList<SortSpec>? sort = null,
        IReadOnlyList<object?>? searchAfter = null,
        long totalHitsThreshold = DefaultTotalHitsThreshold)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);

        _totalHitsThreshold = totalHitsThreshold;
        _capacity = capacity;
        _sort = sort is { Count: > 0 } ? sort : [SortSpec.ByScore];
        _sortByScoreOnly = _sort.Count == 1 && _sort[0].IsScore;
        _searchAfter = searchAfter?.ToArray();
        _heap = new PriorityQueue<CollectedHit, CollectedHit>(Comparer<CollectedHit>.Create(WorstFirst));
    }

    /// <summary>
    /// Matches counted exactly before pruning is allowed to begin. Below this many hits the total
    /// is exact; above it, pruning may skip matches and the total becomes a lower bound. The
    /// trade is deliberate: callers almost always want an exact count for small result sets and
    /// fast ranking for large ones.
    /// </summary>
    public const long DefaultTotalHitsThreshold = 1000;

    public long TotalHits { get; private set; }

    /// <summary>True once pruning has been permitted, meaning <see cref="TotalHits"/> under-counts.</summary>
    public bool TotalIsLowerBound { get; private set; }

    public double MaxScore { get; private set; } = double.NegativeInfinity;

    /// <summary>
    /// Lowest score that can still enter the results, or negative infinity while the heap has room.
    /// Only meaningful when ranking by relevance: under a field sort a low-scoring document can
    /// still rank first, so no score-based pruning is valid.
    /// </summary>
    public double MinCompetitiveScore
    {
        get
        {
            if (!_sortByScoreOnly || _capacity == 0 || _heap.Count < _capacity)
            {
                return double.NegativeInfinity;
            }

            // Keep counting exactly until the threshold is crossed, so ordinary result sets report
            // a true total and only large ones trade accuracy for speed.
            if (TotalHits < _totalHitsThreshold)
            {
                return double.NegativeInfinity;
            }

            TotalIsLowerBound = true;
            return _heap.Peek().Score;
        }
    }

    public void SetSegment(Segment segment, int ordinal, int docBase)
    {
        _segment = segment;
        _segmentOrdinal = ordinal;
        _docBase = docBase;

        _sortColumns = new DocValuesColumn?[_sort.Count];

        for (var i = 0; i < _sort.Count; i++)
        {
            var spec = _sort[i];

            _sortColumns[i] = spec.IsScore || spec.Field == SortSpec.DocIdField
                ? null
                : segment.GetDocValues(spec.Field);
        }
    }

    public void Collect(int docId, double score)
    {
        TotalHits++;

        if (score > MaxScore)
        {
            MaxScore = score;
        }

        if (_capacity == 0)
        {
            return;
        }

        var segment = _segment ?? throw new InvalidOperationException("SetSegment must be called first.");
        var hit = new CollectedHit(
            _segmentOrdinal,
            docId,
            _docBase + docId,
            segment.GetExternalId(docId),
            score,
            BuildSortValues(docId, score, segment));

        // A search-after cursor means the caller already has everything up to this point.
        if (_searchAfter is not null && CompareRank(hit, _searchAfter) <= 0)
        {
            return;
        }

        if (_heap.Count < _capacity)
        {
            _heap.Enqueue(hit, hit);
            return;
        }

        if (CompareRank(hit, _heap.Peek()) < 0)
        {
            _heap.Dequeue();
            _heap.Enqueue(hit, hit);
        }
    }

    private object?[] BuildSortValues(int docId, double score, Segment segment)
    {
        var values = new object?[_sort.Count];

        for (var i = 0; i < _sort.Count; i++)
        {
            var spec = _sort[i];

            values[i] = spec.IsScore
                ? score
                : spec.Field == SortSpec.DocIdField
                    ? segment.GetExternalId(docId)
                    : _sortColumns[i]?.GetSortValue(docId);
        }

        return values;
    }

    public TopDocs GetTopDocs(int from = 0)
    {
        var ordered = new CollectedHit[_heap.Count];

        // Draining a min-heap yields worst-first, so fill the array backwards.
        for (var i = ordered.Length - 1; i >= 0; i--)
        {
            ordered[i] = _heap.Dequeue();
        }

        var page = from >= ordered.Length ? [] : ordered[from..];

        return new TopDocs(
            page,
            TotalHits,
            double.IsNegativeInfinity(MaxScore) ? 0 : MaxScore,
            TotalIsLowerBound);
    }

    /// <summary>Negative when <paramref name="a"/> ranks ahead of <paramref name="b"/>.</summary>
    private int CompareRank(CollectedHit a, CollectedHit b)
    {
        for (var i = 0; i < _sort.Count; i++)
        {
            var comparison = CompareValues(a.SortValues[i], b.SortValues[i]);

            if (comparison != 0)
            {
                return _sort[i].Descending ? -comparison : comparison;
            }
        }

        // Stable final tiebreak so paging cannot repeat or skip a document.
        return a.GlobalDocId.CompareTo(b.GlobalDocId);
    }

    private int CompareRank(CollectedHit hit, object?[] cursor)
    {
        var limit = Math.Min(_sort.Count, cursor.Length);

        for (var i = 0; i < limit; i++)
        {
            var comparison = CompareValues(hit.SortValues[i], cursor[i]);

            if (comparison != 0)
            {
                return _sort[i].Descending ? -comparison : comparison;
            }
        }

        return 0;
    }

    /// <summary>Heap ordering: the worst hit must surface first so it can be evicted.</summary>
    private int WorstFirst(CollectedHit a, CollectedHit b) => CompareRank(b, a);

    /// <summary>
    /// Orders two sort values of unknown static type. Missing values sort last, matching the usual
    /// expectation that documents lacking the sort field appear at the end.
    /// </summary>
    internal static int CompareValues(object? a, object? b)
    {
        if (a is null && b is null)
        {
            return 0;
        }

        if (a is null)
        {
            return 1;
        }

        if (b is null)
        {
            return -1;
        }

        if (a is string sa && b is string sb)
        {
            return string.CompareOrdinal(sa, sb);
        }

        if (TryAsDouble(a, out var da) && TryAsDouble(b, out var db))
        {
            // Scores are compared descending elsewhere; here the raw ascending order is returned.
            return da.CompareTo(db);
        }

        return string.CompareOrdinal(
            Convert.ToString(a, CultureInfo.InvariantCulture),
            Convert.ToString(b, CultureInfo.InvariantCulture));
    }

    private static bool TryAsDouble(object value, out double result)
    {
        switch (value)
        {
            case double d:
                result = d;
                return true;
            case float f:
                result = f;
                return true;
            case long l:
                result = l;
                return true;
            case int i:
                result = i;
                return true;
            default:
                result = 0;
                return false;
        }
    }
}
