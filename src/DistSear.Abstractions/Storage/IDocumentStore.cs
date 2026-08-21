using DistSear.Abstractions.Documents;

namespace DistSear.Abstractions.Storage;

public sealed record ChangeFeedBatch
{
    public IReadOnlyList<IndexedDocument> Documents { get; init; } = [];

    /// <summary>
    /// Resume token. Persisting this after the batch has been applied is what makes indexing
    /// at-least-once and restartable; indexing is an upsert keyed by document id, so replaying a
    /// batch is idempotent.
    /// </summary>
    public string? ContinuationToken { get; init; }

    /// <summary>False when the feed is drained and the reader should back off before polling again.</summary>
    public bool HasMore { get; init; }
}

/// <summary>
/// A cursor over one shard's slice of the change feed. Scoped to a single partition key, so a node
/// reads only the documents belonging to shards it actually owns.
/// </summary>
public interface IChangeFeedCursor : IAsyncDisposable
{
    Task<ChangeFeedBatch> ReadNextAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The durable source of truth for documents. Backed by Cosmos DB in production and by an
/// in-memory implementation in tests.
/// </summary>
public interface IDocumentStore
{
    Task UpsertAsync(IReadOnlyList<IndexedDocument> documents, CancellationToken cancellationToken);

    /// <summary>
    /// Reads specific documents from one shard's partition. Used by the fetch phase when a node's
    /// stored fields are unavailable, and by recovery.
    /// </summary>
    Task<IReadOnlyList<IndexedDocument>> GetAsync(
        string shardKey,
        IReadOnlyList<string> ids,
        CancellationToken cancellationToken);

    /// <summary>
    /// Opens a change-feed cursor for one shard. Pass the token persisted by the previous run to
    /// resume, or null to read from the beginning of the partition.
    /// </summary>
    IChangeFeedCursor OpenChangeFeed(string shardKey, string? continuationToken);
}
