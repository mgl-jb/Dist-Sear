using DistSear.Abstractions.Search;
using DistSear.Abstractions.Transport;
using DistSear.Index.Collectors;

namespace DistSear.Coordinator.Search;

/// <summary>One shard's hit, tagged with the shard it came from so the fetch phase can find it.</summary>
public sealed record MergedHit(int ShardId, ShardDocRef Hit);

/// <summary>
/// Merges per-shard results into a single ranked page.
///
/// The comparison used here must be identical to the one each shard used locally. If they differed,
/// a shard could rank two of its own documents one way while the merge ranked them another, and the
/// resulting page would not be the true global top-k.
/// </summary>
public static class ShardResultMerger
{
    public static IReadOnlyList<MergedHit> Merge(
        IReadOnlyList<ShardQueryResult> results,
        IReadOnlyList<SortSpec> sort,
        int from,
        int size)
    {
        var comparer = new HitComparer(sort);

        var ordered = results
            .SelectMany(r => r.Hits.Select(h => new MergedHit(r.ShardId, h)))
            .Order(comparer)
            .ToList();

        if (from >= ordered.Count)
        {
            return [];
        }

        return ordered.GetRange(from, Math.Min(size, ordered.Count - from));
    }

    private sealed class HitComparer : IComparer<MergedHit>
    {
        private readonly IReadOnlyList<SortSpec> _sort;

        public HitComparer(IReadOnlyList<SortSpec> sort) =>
            _sort = sort is { Count: > 0 } ? sort : [SortSpec.ByScore];

        public int Compare(MergedHit? x, MergedHit? y)
        {
            if (x is null || y is null)
            {
                return x is null ? (y is null ? 0 : 1) : -1;
            }

            for (var i = 0; i < _sort.Count; i++)
            {
                var left = i < x.Hit.SortValues.Count ? x.Hit.SortValues[i] : null;
                var right = i < y.Hit.SortValues.Count ? y.Hit.SortValues[i] : null;

                var comparison = TopDocsCollector.CompareValues(left, right);

                if (comparison != 0)
                {
                    return _sort[i].Descending ? -comparison : comparison;
                }
            }

            // The shards appended the document id as a trailing sort key; comparing it keeps the
            // merged order total, so paging across shards cannot repeat or skip a document.
            var tail = Math.Max(x.Hit.SortValues.Count, y.Hit.SortValues.Count) - 1;

            if (tail >= _sort.Count)
            {
                var comparison = TopDocsCollector.CompareValues(
                    ValueAt(x.Hit.SortValues, tail),
                    ValueAt(y.Hit.SortValues, tail));

                if (comparison != 0)
                {
                    return comparison;
                }
            }

            return string.CompareOrdinal(x.Hit.Id, y.Hit.Id);
        }

        private static object? ValueAt(IReadOnlyList<object?> values, int index) =>
            index >= 0 && index < values.Count ? values[index] : null;
    }
}
