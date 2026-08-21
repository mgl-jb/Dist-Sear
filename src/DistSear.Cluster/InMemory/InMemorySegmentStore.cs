using System.Collections.Concurrent;
using DistSear.Abstractions.Storage;

namespace DistSear.Cluster.InMemory;

/// <summary>
/// In-process stand-in for the Blob-backed segment store. Keeps the same publish ordering: files
/// first, manifest last, so a partially-written snapshot is never visible to a recovering node.
/// </summary>
public sealed class InMemorySegmentStore : ISegmentStore
{
    private readonly ConcurrentDictionary<string, byte[]> _files = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CommitManifest> _manifests = new(StringComparer.Ordinal);

    public Task<CommitManifest?> GetLatestManifestAsync(
        string index,
        int shardId,
        CancellationToken cancellationToken) =>
        Task.FromResult(_manifests.GetValueOrDefault(ShardKey(index, shardId)));

    public Task<Stream> OpenReadAsync(
        string index,
        int shardId,
        long generation,
        string fileName,
        CancellationToken cancellationToken)
    {
        var key = FileKey(index, shardId, generation, fileName);

        if (!_files.TryGetValue(key, out var content))
        {
            throw new FileNotFoundException($"No segment file at {key}.");
        }

        return Task.FromResult<Stream>(new MemoryStream(content, writable: false));
    }

    public async Task WriteFileAsync(
        string index,
        int shardId,
        long generation,
        string fileName,
        Stream content,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);

        _files[FileKey(index, shardId, generation, fileName)] = buffer.ToArray();
    }

    public Task CommitAsync(CommitManifest manifest, CancellationToken cancellationToken)
    {
        var key = ShardKey(manifest.Index, manifest.ShardId);

        _manifests.AddOrUpdate(
            key,
            manifest,

            // Never let an older snapshot replace a newer one, whatever order uploads finish in.
            (_, existing) => manifest.Generation >= existing.Generation ? manifest : existing);

        return Task.CompletedTask;
    }

    public Task DeleteGenerationAsync(
        string index,
        int shardId,
        long generation,
        CancellationToken cancellationToken)
    {
        var prefix = $"{index}/{shardId}/{generation}/";

        foreach (var key in _files.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)))
        {
            _files.TryRemove(key, out _);
        }

        return Task.CompletedTask;
    }

    public int FileCount => _files.Count;

    private static string ShardKey(string index, int shardId) => $"{index}/{shardId}";

    private static string FileKey(string index, int shardId, long generation, string fileName) =>
        $"{index}/{shardId}/{generation}/{fileName}";
}

/// <summary>In-process change-feed checkpoint store.</summary>
public sealed class InMemoryCheckpointStore : ICheckpointStore
{
    private readonly ConcurrentDictionary<string, string> _checkpoints = new(StringComparer.Ordinal);

    public Task<string?> GetAsync(
        string nodeId,
        string index,
        int shardId,
        CancellationToken cancellationToken) =>
        Task.FromResult(_checkpoints.GetValueOrDefault(Key(nodeId, index, shardId)));

    public Task SetAsync(
        string nodeId,
        string index,
        int shardId,
        string continuationToken,
        CancellationToken cancellationToken)
    {
        _checkpoints[Key(nodeId, index, shardId)] = continuationToken;
        return Task.CompletedTask;
    }

    /// <summary>Discards a node's progress, simulating the loss of local state.</summary>
    public void Forget(string nodeId)
    {
        foreach (var key in _checkpoints.Keys.Where(k => k.StartsWith(nodeId + "/", StringComparison.Ordinal)))
        {
            _checkpoints.TryRemove(key, out _);
        }
    }

    private static string Key(string nodeId, string index, int shardId) => $"{nodeId}/{index}/{shardId}";
}
