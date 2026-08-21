using System.Globalization;
using DistSear.Abstractions.Search;
using DistSear.Index.Segments;

namespace DistSear.Index.Facets;

/// <summary>
/// Accumulates aggregation counts over the documents a query matched. Collected in the same pass as
/// the hits, so faceting costs one doc-values lookup per match rather than a second query.
/// </summary>
public interface IFacetCollector
{
    string Name { get; }

    void SetSegment(Segment segment);

    void Collect(int docId);

    FacetResult Build();
}

/// <summary>Counts documents per distinct value of a field.</summary>
public sealed class TermsFacetCollector : IFacetCollector
{
    private readonly FacetSpec _spec;
    private readonly Dictionary<string, long> _counts = new(StringComparer.Ordinal);
    private DocValuesColumn? _column;

    public TermsFacetCollector(FacetSpec spec) => _spec = spec;

    public string Name => _spec.Name;

    public void SetSegment(Segment segment) => _column = segment.GetDocValues(_spec.Field);

    public void Collect(int docId)
    {
        if (_column is null)
        {
            return;
        }

        // Multi-valued fields contribute to one bucket per value, so bucket counts can legitimately
        // sum to more than the number of matching documents.
        foreach (var value in _column.GetValues(docId))
        {
            var key = value as string ?? Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
            _counts[key] = _counts.GetValueOrDefault(key) + 1;
        }
    }

    public FacetResult Build()
    {
        var shardSize = _spec.EffectiveShardSize;

        var ordered = _counts
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .ToList();

        var returned = ordered.Take(shardSize).ToList();

        // The largest bucket this shard did NOT return bounds how far any returned count could be
        // short once the shards are merged: a term ranked below the cutoff here could be present
        // with at most that many documents.
        var errorBound = ordered.Count > shardSize ? ordered[shardSize].Value : 0;
        var otherCount = ordered.Skip(shardSize).Sum(kv => kv.Value);

        return new FacetResult
        {
            Name = _spec.Name,
            Buckets = [.. returned.Select(kv => new FacetBucket(kv.Key, kv.Value))],
            DocCountErrorUpperBound = errorBound,
            SumOtherDocCount = otherCount
        };
    }
}

/// <summary>Counts documents falling into caller-defined numeric ranges.</summary>
public sealed class RangeFacetCollector : IFacetCollector
{
    private readonly FacetSpec _spec;
    private readonly long[] _counts;
    private NumericDocValues? _column;

    public RangeFacetCollector(FacetSpec spec)
    {
        _spec = spec;
        _counts = new long[spec.Ranges.Count];
    }

    public string Name => _spec.Name;

    public void SetSegment(Segment segment) => _column = segment.GetDocValues(_spec.Field) as NumericDocValues;

    public void Collect(int docId)
    {
        if (_column is null || !_column.HasValue(docId))
        {
            return;
        }

        var value = _column.GetDouble(docId);

        for (var i = 0; i < _spec.Ranges.Count; i++)
        {
            var range = _spec.Ranges[i];

            // Half-open [from, to), which is what makes adjacent ranges tile without overlap.
            if ((range.From is null || value >= range.From) && (range.To is null || value < range.To))
            {
                _counts[i]++;
            }
        }
    }

    public FacetResult Build() => new()
    {
        Name = _spec.Name,
        Buckets = [.. _spec.Ranges.Select((r, i) => new FacetBucket(r.Key, _counts[i]))],

        // Ranges are defined by the caller, not discovered from the data, so every shard counts the
        // same buckets and the merged totals are exact.
        DocCountErrorUpperBound = 0,
        SumOtherDocCount = 0
    };
}

/// <summary>Merges per-shard facet results into a single global view.</summary>
public static class FacetMerger
{
    public static FacetResult Merge(string name, IReadOnlyList<FacetResult> parts, int size)
    {
        if (parts.Count == 1)
        {
            var single = parts[0];

            return single with
            {
                Buckets = [.. single.Buckets.Take(size)],
                SumOtherDocCount = single.SumOtherDocCount + single.Buckets.Skip(size).Sum(b => b.Count)
            };
        }

        var totals = new Dictionary<string, long>(StringComparer.Ordinal);
        long errorBound = 0;
        long otherCount = 0;

        foreach (var part in parts)
        {
            foreach (var bucket in part.Buckets)
            {
                totals[bucket.Key] = totals.GetValueOrDefault(bucket.Key) + bucket.Count;
            }

            errorBound += part.DocCountErrorUpperBound;
            otherCount += part.SumOtherDocCount;
        }

        var ordered = totals
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .ToList();

        var returned = ordered.Take(size).ToList();
        otherCount += ordered.Skip(size).Sum(kv => kv.Value);

        return new FacetResult
        {
            Name = name,
            Buckets = [.. returned.Select(kv => new FacetBucket(kv.Key, kv.Value))],
            DocCountErrorUpperBound = errorBound,
            SumOtherDocCount = otherCount
        };
    }
}
