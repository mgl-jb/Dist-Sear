using DistSear.Abstractions.Storage;
using DistSear.Index;
using Microsoft.Extensions.Logging;

namespace DistSear.Node.Shards;

/// <summary>
/// Pumps one shard's slice of the change feed into its local index.
///
/// The cursor is scoped to a single partition key, so this node reads only the documents belonging
/// to the shard it owns. The continuation token is persisted after a batch has been applied, never
/// before: that ordering is what makes delivery at-least-once rather than at-most-once, and since
/// indexing is an upsert keyed by document id, redelivery is harmless.
/// </summary>
public sealed class ShardIndexer : IAsyncDisposable
{
    private readonly IDocumentStore _documents;
    private readonly ICheckpointStore _checkpoints;
    private readonly ShardIndex _index;
    private readonly ILogger? _logger;

    /// <summary>
    /// Held open across batches. Cosmos returns a live iterator whose position advances as it is
    /// read, so recreating it per batch would discard that position and re-establish the feed on
    /// every poll.
    /// </summary>
    private IChangeFeedCursor? _cursor;

    public ShardIndexer(
        string nodeId,
        string indexName,
        int shardId,
        string shardKey,
        IDocumentStore documents,
        ICheckpointStore checkpoints,
        ShardIndex index,
        ILogger? logger = null)
    {
        NodeId = nodeId;
        IndexName = indexName;
        ShardId = shardId;
        ShardKey = shardKey;

        _documents = documents;
        _checkpoints = checkpoints;
        _index = index;
        _logger = logger;
    }

    public string NodeId { get; }

    public string IndexName { get; }

    public int ShardId { get; }

    public string ShardKey { get; }

    /// <summary>Change-feed position of everything applied so far.</summary>
    public string? ContinuationToken { get; private set; }

    public long DocumentsApplied { get; private set; }

    /// <summary>
    /// Positions the reader, either from a restored snapshot or from the beginning. Drops any open
    /// cursor so the next read starts from the requested position.
    /// </summary>
    public void SeekTo(string? continuationToken)
    {
        ContinuationToken = continuationToken;

        var stale = Interlocked.Exchange(ref _cursor, null);

        if (stale is not null)
        {
            _ = stale.DisposeAsync().AsTask();
        }
    }

    /// <summary>
    /// Reads and applies one batch. Returns the number of documents applied, so callers can drain
    /// the feed deterministically instead of waiting on a timer.
    /// </summary>
    public async Task<int> PumpOnceAsync(CancellationToken cancellationToken)
    {
        _cursor ??= _documents.OpenChangeFeed(ShardKey, ContinuationToken);

        var batch = await _cursor.ReadNextAsync(cancellationToken);

        if (batch.Documents.Count == 0)
        {
            // Still advance the token: an empty read can legitimately move the position forward.
            if (batch.ContinuationToken is not null)
            {
                ContinuationToken = batch.ContinuationToken;
            }

            return 0;
        }

        foreach (var document in batch.Documents)
        {
            _index.AddOrUpdate(document);
        }

        DocumentsApplied += batch.Documents.Count;
        ContinuationToken = batch.ContinuationToken;

        if (ContinuationToken is not null)
        {
            await _checkpoints.SetAsync(NodeId, IndexName, ShardId, ContinuationToken, cancellationToken);
        }

        _logger?.LogDebug(
            "Applied {Count} documents to {Index}/{Shard}; now at {Token}.",
            batch.Documents.Count,
            IndexName,
            ShardId,
            ContinuationToken);

        return batch.Documents.Count;
    }

    public async ValueTask DisposeAsync()
    {
        var cursor = Interlocked.Exchange(ref _cursor, null);

        if (cursor is not null)
        {
            await cursor.DisposeAsync();
        }
    }

    /// <summary>Reads until the feed is drained. Returns the total applied.</summary>
    public async Task<int> DrainAsync(CancellationToken cancellationToken)
    {
        var total = 0;

        while (true)
        {
            var applied = await PumpOnceAsync(cancellationToken);
            total += applied;

            if (applied == 0)
            {
                return total;
            }
        }
    }
}
