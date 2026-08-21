using DistSear.Abstractions.Cluster;
using DistSear.Index.Segments;

namespace DistSear.Index.Scoring;

/// <summary>
/// Corpus statistics drawn from the segments of a single shard. This is the default: scoring uses
/// only what this shard can see, which is fast but means a document's score depends on which shard
/// it landed on. <c>DfsQueryThenFetch</c> replaces it with globally-summed statistics.
/// </summary>
public sealed class SegmentTermStatistics : ITermStatisticsProvider
{
    private readonly IReadOnlyList<Segment> _segments;
    private readonly Dictionary<string, double> _averageFieldLengths = new(StringComparer.Ordinal);

    public SegmentTermStatistics(IReadOnlyList<Segment> segments)
    {
        _segments = segments;

        long liveDocuments = 0;

        foreach (var segment in segments)
        {
            liveDocuments += segment.LiveDocs.LiveCount;
        }

        DocumentCount = liveDocuments;
    }

    public long DocumentCount { get; }

    public long DocumentFrequency(string field, string term)
    {
        long total = 0;

        foreach (var segment in _segments)
        {
            total += segment.GetField(field)?.DocumentFrequency(term) ?? 0;
        }

        return total;
    }

    public double AverageFieldLength(string field)
    {
        if (_averageFieldLengths.TryGetValue(field, out var cached))
        {
            return cached;
        }

        long sumLength = 0;
        long documents = 0;

        foreach (var segment in _segments)
        {
            if (segment.GetField(field) is { } terms)
            {
                sumLength += terms.SumFieldLength;
                documents += terms.DocumentCount;
            }
        }

        var average = documents > 0 ? (double)sumLength / documents : 0;
        _averageFieldLengths[field] = average;

        return average;
    }

    /// <summary>
    /// Packages this shard's statistics for the coordinator's DFS pre-pass. Only the terms the
    /// query will actually look up are included, so the payload stays proportional to the query
    /// rather than to the dictionary.
    /// </summary>
    public CollectionStatistics Export(IEnumerable<(string Field, string Term)> terms)
    {
        var frequencies = new Dictionary<string, long>(StringComparer.Ordinal);
        var fields = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (field, term) in terms)
        {
            frequencies[CollectionStatistics.TermKey(field, term)] = DocumentFrequency(field, term);
            fields.Add(field);
        }

        var averages = new Dictionary<string, double>(StringComparer.Ordinal);

        foreach (var field in fields)
        {
            averages[field] = AverageFieldLength(field);
        }

        return new CollectionStatistics
        {
            DocCount = DocumentCount,
            DocFrequency = frequencies,
            AverageFieldLength = averages
        };
    }
}
