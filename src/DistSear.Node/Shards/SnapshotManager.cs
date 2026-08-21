using DistSear.Abstractions.Storage;
using DistSear.Index;
using DistSear.Index.Segments;

namespace DistSear.Node.Shards;

/// <summary>
/// Moves a shard's segments between memory and durable storage.
///
/// The manifest is written last, after every file it names has been uploaded. A recovering replica
/// only ever sees a manifest whose files are all present, so a snapshot interrupted halfway leaves
/// the previous one intact rather than producing a corrupt half-snapshot.
/// </summary>
public sealed class SnapshotManager
{
    private readonly ISegmentStore _store;

    public SnapshotManager(ISegmentStore store) => _store = store;

    public static string SegmentFileName(int ordinal) => $"segment-{ordinal:D5}.dss";

    /// <summary>
    /// Uploads the shard's sealed segments and publishes a manifest recording the change-feed
    /// position they represent. That token is what turns recovery into a download plus a short
    /// catch-up instead of a replay from the beginning of the partition.
    /// </summary>
    public async Task<CommitManifest> SaveAsync(
        string index,
        int shardId,
        ShardIndex shard,
        string? continuationToken,
        long generation,
        CancellationToken cancellationToken)
    {
        var segments = shard.SealedSegments();
        var files = new List<string>(segments.Count);

        for (var i = 0; i < segments.Count; i++)
        {
            var fileName = SegmentFileName(i);

            using var buffer = new MemoryStream();
            SegmentSerializer.Write(segments[i], buffer);
            buffer.Position = 0;

            await _store.WriteFileAsync(index, shardId, generation, fileName, buffer, cancellationToken);
            files.Add(fileName);
        }

        var manifest = new CommitManifest
        {
            Index = index,
            ShardId = shardId,
            Generation = generation,
            Files = files,
            ContinuationToken = continuationToken,
            DocumentCount = segments.Sum(s => s.LiveDocs.LiveCount),
            CreatedAt = DateTimeOffset.UtcNow
        };

        await _store.CommitAsync(manifest, cancellationToken);
        return manifest;
    }

    /// <summary>
    /// Restores the newest snapshot into an empty shard. Returns the change-feed token the restored
    /// state corresponds to, or null when there is no snapshot and the shard must replay in full.
    /// </summary>
    public async Task<string?> TryRestoreAsync(
        string index,
        int shardId,
        ShardIndex shard,
        CancellationToken cancellationToken)
    {
        var manifest = await _store.GetLatestManifestAsync(index, shardId, cancellationToken);

        if (manifest is null)
        {
            return null;
        }

        var segments = new List<Segment>(manifest.Files.Count);

        foreach (var file in manifest.Files)
        {
            await using var stream = await _store.OpenReadAsync(
                index,
                shardId,
                manifest.Generation,
                file,
                cancellationToken);

            // BinaryReader needs a seekable stream, and blob reads are not always seekable.
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken);
            buffer.Position = 0;

            segments.Add(SegmentSerializer.Read(buffer));
        }

        shard.RestoreSegments(segments);
        return manifest.ContinuationToken;
    }
}
