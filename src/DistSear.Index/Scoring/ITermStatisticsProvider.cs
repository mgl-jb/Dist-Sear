using DistSear.Abstractions.Cluster;

namespace DistSear.Index.Scoring;

/// <summary>
/// Supplies the corpus statistics BM25 needs. The indirection is the seam between per-shard scoring
/// and globally-consistent scoring: the same query code runs either way, and only the statistics
/// behind it change.
/// </summary>
public interface ITermStatisticsProvider
{
    long DocumentCount { get; }

    long DocumentFrequency(string field, string term);

    double AverageFieldLength(string field);
}

/// <summary>
/// Wraps global statistics gathered by the DFS pre-pass, falling back to the shard's own view for
/// anything the pre-pass did not collect (for example terms produced by a rewritten wildcard).
/// </summary>
public sealed class GlobalTermStatistics : ITermStatisticsProvider
{
    private readonly CollectionStatistics _global;
    private readonly ITermStatisticsProvider _fallback;

    public GlobalTermStatistics(CollectionStatistics global, ITermStatisticsProvider fallback)
    {
        _global = global;
        _fallback = fallback;
    }

    public long DocumentCount => _global.DocCount > 0 ? _global.DocCount : _fallback.DocumentCount;

    public long DocumentFrequency(string field, string term) =>
        _global.DocFrequency.TryGetValue(CollectionStatistics.TermKey(field, term), out var df)
            ? df
            : _fallback.DocumentFrequency(field, term);

    public double AverageFieldLength(string field) =>
        _global.AverageFieldLength.TryGetValue(field, out var length) && length > 0
            ? length
            : _fallback.AverageFieldLength(field);
}
