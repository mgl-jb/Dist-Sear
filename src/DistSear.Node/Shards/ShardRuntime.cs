using DistSear.Abstractions.Cluster;
using DistSear.Abstractions.Mapping;
using DistSear.Abstractions.Storage;
using DistSear.Analysis;
using DistSear.Cluster.Routing;
using DistSear.Index;
using Microsoft.Extensions.Logging;

namespace DistSear.Node.Shards;

/// <summary>
/// One shard as it lives on one node: its index, its change-feed reader, and its snapshot lifecycle.
///
/// Startup is the recovery path, not an exceptional one. Container Apps replicas have ephemeral
/// disks, so every start restores the newest snapshot and replays the feed from the token that
/// snapshot recorded. Making recovery the normal path means it is exercised constantly rather than
/// only during an incident.
/// </summary>
public sealed class ShardRuntime
{
    private readonly SnapshotManager _snapshots;
    private readonly ILogger? _logger;
    private long _snapshotGeneration;

    private ShardRuntime(
        string nodeId,
        IndexMapping mapping,
        int shardId,
        ShardIndex index,
        ShardIndexer indexer,
        SnapshotManager snapshots,
        ILogger? logger)
    {
        NodeId = nodeId;
        Mapping = mapping;
        ShardId = shardId;
        Index = index;
        Indexer = indexer;

        _snapshots = snapshots;
        _logger = logger;
    }

    public string NodeId { get; }

    public IndexMapping Mapping { get; }

    public string IndexName => Mapping.Name;

    public int ShardId { get; }

    public ShardIndex Index { get; }

    public ShardIndexer Indexer { get; }

    public ShardState State { get; private set; } = ShardState.Initializing;

    /// <summary>Whether this copy is the one responsible for uploading snapshots.</summary>
    public bool IsSnapshotOwner { get; set; }

    /// <summary>
    /// Restores from the newest snapshot, then replays the change feed until caught up. The shard
    /// only reports <see cref="ShardState.Started"/> once it is current, so the coordinator never
    /// routes a query to a half-built replica.
    /// </summary>
    public static async Task<ShardRuntime> StartAsync(
        string nodeId,
        IndexMapping mapping,
        int shardId,
        AnalyzerRegistry analyzers,
        IDocumentStore documents,
        ICheckpointStore checkpoints,
        ISegmentStore segments,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        var index = new ShardIndex(mapping, analyzers, shardId);
        var snapshots = new SnapshotManager(segments);

        var indexer = new ShardIndexer(
            nodeId,
            mapping.Name,
            shardId,
            DocumentRouter.ShardKey(shardId),
            documents,
            checkpoints,
            index,
            logger);

        var runtime = new ShardRuntime(nodeId, mapping, shardId, index, indexer, snapshots, logger);
        await runtime.RecoverAsync(cancellationToken);

        return runtime;
    }

    private async Task RecoverAsync(CancellationToken cancellationToken)
    {
        State = ShardState.Recovering;

        var token = await _snapshots.TryRestoreAsync(IndexName, ShardId, Index, cancellationToken);

        if (token is not null)
        {
            _logger?.LogInformation(
                "Restored {Index}/{Shard} from snapshot at token {Token}.",
                IndexName,
                ShardId,
                token);
        }

        // Without a snapshot the shard replays its partition from the beginning, which is correct
        // but slow -- exactly what snapshots exist to avoid.
        Indexer.SeekTo(token);

        await Indexer.DrainAsync(cancellationToken);
        Index.Refresh();

        State = ShardState.Started;
    }

    /// <summary>Applies any new changes and republishes the reader.</summary>
    public async Task<int> CatchUpAsync(CancellationToken cancellationToken)
    {
        var applied = await Indexer.DrainAsync(cancellationToken);

        if (applied > 0)
        {
            Index.Refresh();
        }

        return applied;
    }

    /// <summary>
    /// Uploads a snapshot, but only from the copy nominated to do so. Skipping on the other
    /// replicas is the whole point of nominating one: they would otherwise upload identical bytes.
    /// </summary>
    public async Task<CommitManifest?> SnapshotAsync(CancellationToken cancellationToken)
    {
        if (!IsSnapshotOwner)
        {
            return null;
        }

        var manifest = await _snapshots.SaveAsync(
            IndexName,
            ShardId,
            Index,
            Indexer.ContinuationToken,
            ++_snapshotGeneration,
            cancellationToken);

        _logger?.LogInformation(
            "Snapshotted {Index}/{Shard} generation {Generation} with {Count} documents.",
            IndexName,
            ShardId,
            manifest.Generation,
            manifest.DocumentCount);

        return manifest;
    }

    public void MarkUnavailable() => State = ShardState.Unassigned;
}
