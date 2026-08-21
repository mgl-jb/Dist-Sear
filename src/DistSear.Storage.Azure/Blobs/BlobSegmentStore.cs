using System.Text.Json;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using DistSear.Abstractions.Storage;
using Microsoft.Extensions.Options;

namespace DistSear.Storage.Azure.Blobs;

/// <summary>
/// Segment snapshots in Azure Blob Storage.
///
/// Layout is <c>{index}/{shard}/{generation}/segment-NNNNN.dss</c> with the manifest alongside at
/// <c>{index}/{shard}/manifest.json</c>. The manifest is written last, after every file it names has
/// been uploaded, so a recovering replica can never see a snapshot whose files are only partly
/// there — an interrupted upload leaves the previous snapshot intact and usable.
/// </summary>
public sealed class BlobSegmentStore : ISegmentStore
{
    private readonly BlobContainerClient _container;

    public BlobSegmentStore(BlobServiceClient client, IOptions<AzureStorageOptions> options) =>
        _container = client.GetBlobContainerClient(options.Value.SnapshotsContainer);

    public async Task<CommitManifest?> GetLatestManifestAsync(
        string index,
        int shardId,
        CancellationToken cancellationToken)
    {
        var blob = _container.GetBlobClient(ManifestPath(index, shardId));

        try
        {
            var response = await blob.DownloadContentAsync(cancellationToken);
            return JsonSerializer.Deserialize<CommitManifest>(response.Value.Content.ToMemory().Span);
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        {
            // No snapshot yet: the shard rebuilds by replaying its partition in full.
            return null;
        }
    }

    public async Task<Stream> OpenReadAsync(
        string index,
        int shardId,
        long generation,
        string fileName,
        CancellationToken cancellationToken)
    {
        var blob = _container.GetBlobClient(FilePath(index, shardId, generation, fileName));
        return await blob.OpenReadAsync(cancellationToken: cancellationToken);
    }

    public async Task WriteFileAsync(
        string index,
        int shardId,
        long generation,
        string fileName,
        Stream content,
        CancellationToken cancellationToken)
    {
        var blob = _container.GetBlobClient(FilePath(index, shardId, generation, fileName));
        await blob.UploadAsync(content, overwrite: true, cancellationToken);
    }

    public async Task CommitAsync(CommitManifest manifest, CancellationToken cancellationToken)
    {
        var blob = _container.GetBlobClient(ManifestPath(manifest.Index, manifest.ShardId));
        var payload = JsonSerializer.SerializeToUtf8Bytes(manifest);

        await blob.UploadAsync(new BinaryData(payload), overwrite: true, cancellationToken);
    }

    public async Task DeleteGenerationAsync(
        string index,
        int shardId,
        long generation,
        CancellationToken cancellationToken)
    {
        var prefix = $"{index}/{shardId}/{generation}/";

        await foreach (var item in _container.GetBlobsAsync(
            BlobTraits.None,
            BlobStates.None,
            prefix,
            cancellationToken))
        {
            await _container.GetBlobClient(item.Name).DeleteIfExistsAsync(
                cancellationToken: cancellationToken);
        }
    }

    /// <summary>Creates the container if it is missing. Safe to call on every start.</summary>
    public Task EnsureCreatedAsync(CancellationToken cancellationToken) =>
        _container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);

    private static string ManifestPath(string index, int shardId) => $"{index}/{shardId}/manifest.json";

    private static string FilePath(string index, int shardId, long generation, string fileName) =>
        $"{index}/{shardId}/{generation}/{fileName}";
}

/// <summary>
/// Change-feed checkpoints as small blobs, one per node and shard. Separate from the snapshot
/// manifest because it advances far more often: the manifest records where a snapshot was taken,
/// while this records how far the node has read since.
/// </summary>
public sealed class BlobCheckpointStore : ICheckpointStore
{
    private readonly BlobContainerClient _container;

    public BlobCheckpointStore(BlobServiceClient client, IOptions<AzureStorageOptions> options) =>
        _container = client.GetBlobContainerClient(options.Value.SnapshotsContainer);

    public async Task<string?> GetAsync(
        string nodeId,
        string index,
        int shardId,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await _container
                .GetBlobClient(Path(nodeId, index, shardId))
                .DownloadContentAsync(cancellationToken);

            return response.Value.Content.ToString();
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        {
            return null;
        }
    }

    public async Task SetAsync(
        string nodeId,
        string index,
        int shardId,
        string continuationToken,
        CancellationToken cancellationToken) =>
        await _container
            .GetBlobClient(Path(nodeId, index, shardId))
            .UploadAsync(new BinaryData(continuationToken), overwrite: true, cancellationToken);

    private static string Path(string nodeId, string index, int shardId) =>
        $"_checkpoints/{nodeId}/{index}/{shardId}.token";
}
