using DistSear.Abstractions.Documents;
using DistSear.Abstractions.Mapping;
using DistSear.Analysis;
using DistSear.Index.Query;
using DistSear.Index.Scoring;
using DistSear.Index.Segments;

namespace DistSear.Index;

/// <summary>
/// The writable index for one shard: a mutable in-memory buffer in front of a list of immutable
/// segments.
///
/// Writes land in the buffer and are invisible until <see cref="Refresh"/> seals it into a segment
/// and publishes a new reader. That is the near-real-time model: visibility is controlled by refresh
/// cadence rather than by every write paying the cost of becoming searchable.
/// </summary>
public sealed class ShardIndex
{
    private readonly Lock _gate = new();
    private readonly List<Segment> _segments = [];

    /// <summary>External document id to its home in a sealed segment.</summary>
    private readonly Dictionary<string, (int Segment, int Doc)> _locations = new(StringComparer.Ordinal);

    /// <summary>External document id to its position in the unsealed buffer.</summary>
    private readonly Dictionary<string, int> _buffered = new(StringComparer.Ordinal);

    /// <summary>Buffer positions superseded before the buffer was sealed; tombstoned on refresh.</summary>
    private readonly List<int> _supersededInBuffer = [];

    private SegmentBuilder _buffer;
    private IndexSearcher _searcher;

    public ShardIndex(IndexMapping mapping, AnalyzerRegistry analyzers, int shardId = 0)
    {
        Mapping = mapping;
        Analyzers = analyzers;
        ShardId = shardId;

        _buffer = new SegmentBuilder(mapping, analyzers);
        _searcher = new IndexSearcher([]);
    }

    public IndexMapping Mapping { get; }

    public AnalyzerRegistry Analyzers { get; }

    public int ShardId { get; }

    /// <summary>Bumped on every refresh. Used to invalidate cached query results.</summary>
    public long Generation { get; private set; }

    public int BufferedDocumentCount
    {
        get
        {
            lock (_gate)
            {
                return _buffer.DocumentCount;
            }
        }
    }

    public long BufferedBytes
    {
        get
        {
            lock (_gate)
            {
                return _buffer.EstimatedBytes;
            }
        }
    }

    /// <summary>
    /// Adds or replaces a document. A replacement tombstones the previous copy immediately, so a
    /// reader created after this call never sees both versions.
    /// </summary>
    public void AddOrUpdate(IndexedDocument document)
    {
        lock (_gate)
        {
            if (document.Deleted)
            {
                DeleteCore(document.Id);
                return;
            }

            RetireExisting(document.Id);

            var docId = _buffer.AddDocument(document);
            _buffered[document.Id] = docId;
        }
    }

    public bool Delete(string id)
    {
        lock (_gate)
        {
            return DeleteCore(id);
        }
    }

    private bool DeleteCore(string id)
    {
        var existed = RetireExisting(id);
        return existed;
    }

    /// <summary>Removes any current copy of the id, whether sealed or still buffered.</summary>
    private bool RetireExisting(string id)
    {
        var found = false;

        if (_locations.Remove(id, out var location))
        {
            _segments[location.Segment].LiveDocs.Delete(location.Doc);
            found = true;
        }

        if (_buffered.Remove(id, out var bufferedDoc))
        {
            // The buffer cannot be edited, so remember the position and tombstone it once sealed.
            _supersededInBuffer.Add(bufferedDoc);
            found = true;
        }

        return found;
    }

    /// <summary>
    /// Seals the buffer and publishes a new reader. Cheap enough to run on a timer, which is what
    /// makes sub-second visibility affordable.
    /// </summary>
    public IndexSearcher Refresh()
    {
        lock (_gate)
        {
            if (_buffer.DocumentCount > 0)
            {
                var segment = _buffer.Build();
                var ordinal = _segments.Count;
                _segments.Add(segment);

                foreach (var docId in _supersededInBuffer)
                {
                    segment.LiveDocs.Delete(docId);
                }

                foreach (var (id, docId) in _buffered)
                {
                    _locations[id] = (ordinal, docId);
                }

                _buffer = new SegmentBuilder(Mapping, Analyzers);
                _buffered.Clear();
                _supersededInBuffer.Clear();
            }

            Generation++;
            _searcher = new IndexSearcher([.. _segments]);
            return _searcher;
        }
    }

    /// <summary>
    /// Adopts segments restored from a snapshot. Used by recovery: the shard is rebuilt from
    /// durable storage before the change feed is replayed on top, so a replica returns to service
    /// in seconds rather than replaying its entire partition.
    /// </summary>
    public void RestoreSegments(IEnumerable<Segment> segments)
    {
        lock (_gate)
        {
            if (_segments.Count > 0 || _buffer.DocumentCount > 0)
            {
                throw new InvalidOperationException("Segments can only be restored into an empty shard.");
            }

            foreach (var segment in segments)
            {
                var ordinal = _segments.Count;
                _segments.Add(segment);

                for (var docId = 0; docId < segment.MaxDoc; docId++)
                {
                    if (segment.LiveDocs.IsLive(docId))
                    {
                        _locations[segment.GetExternalId(docId)] = (ordinal, docId);
                    }
                }
            }

            Generation++;
            _searcher = new IndexSearcher([.. _segments]);
        }
    }

    /// <summary>Sealed segments, for snapshotting. Excludes anything still in the write buffer.</summary>
    public IReadOnlyList<Segment> SealedSegments()
    {
        lock (_gate)
        {
            return [.. _segments];
        }
    }

    /// <summary>The most recently published reader. Never blocks on writers.</summary>
    public IndexSearcher Searcher => _searcher;

    public SearchContext CreateContext(ITermStatisticsProvider? statistics = null) => new()
    {
        Mapping = Mapping,
        Analyzers = Analyzers,
        Statistics = statistics ?? new SegmentTermStatistics(_searcher.Segments)
    };

    public SegmentTermStatistics LocalStatistics() => new(_searcher.Segments);

    public int SegmentCount
    {
        get
        {
            lock (_gate)
            {
                return _segments.Count;
            }
        }
    }

    public int DocumentCount => _searcher.LiveDocCount;

    /// <summary>Reads a stored document from a sealed segment. Backs the fetch phase.</summary>
    public IReadOnlyDictionary<string, object?>? GetStoredFields(string id)
    {
        lock (_gate)
        {
            return _locations.TryGetValue(id, out var location)
                ? _segments[location.Segment].StoredFields[location.Doc]
                : null;
        }
    }
}
