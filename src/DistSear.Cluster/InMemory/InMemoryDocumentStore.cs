using System.Collections.Concurrent;
using DistSear.Abstractions.Documents;
using DistSear.Abstractions.Storage;

namespace DistSear.Cluster.InMemory;

/// <summary>
/// In-process stand-in for the Cosmos DB document store, including its change feed.
///
/// Each partition keeps an append-only log, which is what a change feed fundamentally is: a
/// continuation token is a position in that log, so resuming, replaying and checkpointing behave
/// the same way they do against Cosmos. This is what lets the whole cluster — routing, replication,
/// recovery — be exercised in a unit test with no emulator running.
/// </summary>
public sealed class InMemoryDocumentStore : IDocumentStore
{
    private readonly ConcurrentDictionary<string, Partition> _partitions = new(StringComparer.Ordinal);

    /// <summary>Documents returned per change-feed batch, mirroring a page size.</summary>
    public int BatchSize { get; init; } = 100;

    public Task UpsertAsync(IReadOnlyList<IndexedDocument> documents, CancellationToken cancellationToken)
    {
        foreach (var document in documents)
        {
            var partition = _partitions.GetOrAdd(document.ShardKey, _ => new Partition());
            partition.Append(document);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<IndexedDocument>> GetAsync(
        string shardKey,
        IReadOnlyList<string> ids,
        CancellationToken cancellationToken)
    {
        if (!_partitions.TryGetValue(shardKey, out var partition))
        {
            return Task.FromResult<IReadOnlyList<IndexedDocument>>([]);
        }

        return Task.FromResult(partition.Get(ids));
    }

    public IChangeFeedCursor OpenChangeFeed(string shardKey, string? continuationToken) =>
        new Cursor(_partitions.GetOrAdd(shardKey, _ => new Partition()), continuationToken, BatchSize);

    /// <summary>Total documents currently stored, for assertions.</summary>
    public int DocumentCount => _partitions.Values.Sum(p => p.LiveCount);

    private sealed class Partition
    {
        private readonly Lock _gate = new();
        private readonly List<IndexedDocument> _log = [];
        private readonly Dictionary<string, IndexedDocument> _latest = new(StringComparer.Ordinal);

        private long _version;

        public void Append(IndexedDocument document)
        {
            lock (_gate)
            {
                var versioned = document with { Version = ++_version };
                _log.Add(versioned);
                _latest[document.Id] = versioned;
            }
        }

        public IReadOnlyList<IndexedDocument> Get(IReadOnlyList<string> ids)
        {
            lock (_gate)
            {
                var results = new List<IndexedDocument>(ids.Count);

                foreach (var id in ids)
                {
                    if (_latest.TryGetValue(id, out var document) && !document.Deleted)
                    {
                        results.Add(document);
                    }
                }

                return results;
            }
        }

        public (IReadOnlyList<IndexedDocument> Documents, int NextPosition, bool HasMore) Read(
            int position,
            int batchSize)
        {
            lock (_gate)
            {
                if (position >= _log.Count)
                {
                    return ([], position, false);
                }

                var take = Math.Min(batchSize, _log.Count - position);
                var slice = _log.GetRange(position, take);

                return (slice, position + take, position + take < _log.Count);
            }
        }

        public int LiveCount
        {
            get
            {
                lock (_gate)
                {
                    return _latest.Values.Count(d => !d.Deleted);
                }
            }
        }
    }

    private sealed class Cursor : IChangeFeedCursor
    {
        private readonly Partition _partition;
        private readonly int _batchSize;
        private int _position;

        public Cursor(Partition partition, string? continuationToken, int batchSize)
        {
            _partition = partition;
            _batchSize = batchSize;
            _position = int.TryParse(continuationToken, out var parsed) ? parsed : 0;
        }

        public Task<ChangeFeedBatch> ReadNextAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (documents, next, hasMore) = _partition.Read(_position, _batchSize);
            _position = next;

            return Task.FromResult(new ChangeFeedBatch
            {
                Documents = documents,
                ContinuationToken = next.ToString(),
                HasMore = hasMore
            });
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
