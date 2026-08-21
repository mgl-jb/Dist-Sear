namespace DistSear.Abstractions.Storage;

/// <summary>
/// Describes one durable snapshot of a shard's index: the segment files it comprises, and the
/// change-feed position they represent. A recovering node downloads the newest manifest, restores
/// its files, and resumes the feed from <see cref="ContinuationToken"/> — turning recovery from a
/// full replay into a download plus a short catch-up.
/// </summary>
public sealed record CommitManifest
{
    public required string Index { get; init; }

    public required int ShardId { get; init; }

    /// <summary>Monotonically increasing snapshot number.</summary>
    public required long Generation { get; init; }

    public required IReadOnlyList<string> Files { get; init; }

    public required string? ContinuationToken { get; init; }

    public long DocumentCount { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>Durable segment storage, backed by Azure Blob Storage in production.</summary>
public interface ISegmentStore
{
    Task<CommitManifest?> GetLatestManifestAsync(
        string index,
        int shardId,
        CancellationToken cancellationToken);

    Task<Stream> OpenReadAsync(
        string index,
        int shardId,
        long generation,
        string fileName,
        CancellationToken cancellationToken);

    Task WriteFileAsync(
        string index,
        int shardId,
        long generation,
        string fileName,
        Stream content,
        CancellationToken cancellationToken);

    /// <summary>
    /// Publishes the manifest. Written last, after every file it references has been uploaded, so a
    /// partially-uploaded snapshot is never visible to a recovering node.
    /// </summary>
    Task CommitAsync(CommitManifest manifest, CancellationToken cancellationToken);

    Task DeleteGenerationAsync(
        string index,
        int shardId,
        long generation,
        CancellationToken cancellationToken);
}

/// <summary>
/// Per-node, per-shard change-feed position, checkpointed between snapshots so a restart does not
/// replay everything written since the last snapshot.
/// </summary>
public interface ICheckpointStore
{
    Task<string?> GetAsync(string nodeId, string index, int shardId, CancellationToken cancellationToken);

    Task SetAsync(
        string nodeId,
        string index,
        int shardId,
        string continuationToken,
        CancellationToken cancellationToken);
}
