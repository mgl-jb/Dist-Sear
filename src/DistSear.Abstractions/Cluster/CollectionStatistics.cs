namespace DistSear.Abstractions.Cluster;

/// <summary>
/// Corpus-level statistics used by BM25. Each shard can supply its own; the coordinator sums them
/// into a global view for <c>DfsQueryThenFetch</c> so that identical documents score identically
/// regardless of which shard they landed on.
/// </summary>
public sealed record CollectionStatistics
{
    public long DocCount { get; init; }

    /// <summary>Mean field length in terms, per field. Denominator of the BM25 length normalisation.</summary>
    public IReadOnlyDictionary<string, double> AverageFieldLength { get; init; } =
        new Dictionary<string, double>();

    /// <summary>Document frequency keyed by <see cref="TermKey"/>.</summary>
    public IReadOnlyDictionary<string, long> DocFrequency { get; init; } =
        new Dictionary<string, long>();

    /// <summary>
    /// Wire-safe composite key for a (field, term) pair. Length-prefixed so that the pair is
    /// recovered unambiguously even when a term contains the separator.
    /// </summary>
    public static string TermKey(string field, string term) =>
        string.Concat(field.Length.ToString(), ":", field, term);

    /// <summary>Sums per-shard statistics into a single global view.</summary>
    public static CollectionStatistics Merge(IEnumerable<CollectionStatistics> parts)
    {
        long docCount = 0;
        var df = new Dictionary<string, long>(StringComparer.Ordinal);
        var lengthSum = new Dictionary<string, double>(StringComparer.Ordinal);
        var lengthWeight = new Dictionary<string, double>(StringComparer.Ordinal);

        foreach (var part in parts)
        {
            docCount += part.DocCount;

            foreach (var (key, value) in part.DocFrequency)
            {
                df[key] = df.GetValueOrDefault(key) + value;
            }

            // Average field length is a weighted mean: each shard's average carries the weight of
            // its own document count, otherwise a tiny shard would skew the global figure.
            foreach (var (field, average) in part.AverageFieldLength)
            {
                lengthSum[field] = lengthSum.GetValueOrDefault(field) + (average * part.DocCount);
                lengthWeight[field] = lengthWeight.GetValueOrDefault(field) + part.DocCount;
            }
        }

        var averages = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (field, sum) in lengthSum)
        {
            var weight = lengthWeight[field];
            averages[field] = weight > 0 ? sum / weight : 0;
        }

        return new CollectionStatistics
        {
            DocCount = docCount,
            DocFrequency = df,
            AverageFieldLength = averages
        };
    }
}
