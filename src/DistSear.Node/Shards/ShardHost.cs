using System.Collections.Concurrent;
using DistSear.Abstractions.Cluster;
using DistSear.Abstractions.Mapping;
using DistSear.Abstractions.Storage;
using DistSear.Analysis;
using DistSear.Cluster;
using Microsoft.Extensions.Logging;

namespace DistSear.Node.Shards;

/// <summary>
/// Every shard this node currently owns.
///
/// The node does not decide what it owns: it reads the allocation table and converges on it,
/// starting shards that have been assigned and dropping those that have not. Keeping the node
/// purely reactive is what allows the elected coordinator to be the single authority on placement.
/// </summary>
public sealed class ShardHost
{
    private readonly ConcurrentDictionary<ShardKey, ShardRuntime> _shards = new();
    private readonly SemaphoreSlim _reconcileGate = new(1, 1);

    private readonly string _nodeId;
    private readonly AnalyzerRegistry _analyzers;
    private readonly IDocumentStore _documents;
    private readonly ICheckpointStore _checkpoints;
    private readonly ISegmentStore _segments;
    private readonly ClusterController _controller;
    private readonly ILogger<ShardHost>? _logger;

    public ShardHost(
        string nodeId,
        AnalyzerRegistry analyzers,
        IDocumentStore documents,
        ICheckpointStore checkpoints,
        ISegmentStore segments,
        ClusterController controller,
        ILogger<ShardHost>? logger = null)
    {
        _nodeId = nodeId;
        _analyzers = analyzers;
        _documents = documents;
        _checkpoints = checkpoints;
        _segments = segments;
        _controller = controller;
        _logger = logger;
    }

    public string NodeId => _nodeId;

    public IReadOnlyCollection<ShardRuntime> Shards => [.. _shards.Values];

    public ShardRuntime? Find(string index, int shardId) =>
        _shards.GetValueOrDefault(new ShardKey(index, shardId));

    /// <summary>
    /// Brings this node's shards into line with the allocation table: starts anything newly
    /// assigned, drops anything taken away, and reports readiness back to the coordinator.
    /// </summary>
    public async Task ReconcileAsync(ClusterState state, CancellationToken cancellationToken)
    {
        await _reconcileGate.WaitAsync(cancellationToken);

        try
        {
            var assigned = new HashSet<ShardKey>();

            foreach (var allocation in state.Shards)
            {
                var copy = allocation.Copies.FirstOrDefault(c =>
                    string.Equals(c.NodeId, _nodeId, StringComparison.Ordinal));

                if (copy is null || !state.Indexes.TryGetValue(allocation.Index, out var metadata))
                {
                    continue;
                }

                var key = new ShardKey(allocation.Index, allocation.ShardId);
                assigned.Add(key);

                if (_shards.TryGetValue(key, out var existing))
                {
                    existing.IsSnapshotOwner = copy.IsSnapshotOwner;
                    continue;
                }

                await StartShardAsync(key, metadata.Mapping, copy.IsSnapshotOwner, cancellationToken);
            }

            // Anything no longer assigned belongs to another node now.
            foreach (var key in _shards.Keys.Where(k => !assigned.Contains(k)).ToList())
            {
                if (_shards.TryRemove(key, out var dropped))
                {
                    dropped.MarkUnavailable();
                    _logger?.LogInformation("Released {Index}/{Shard}.", key.Index, key.ShardId);
                }
            }
        }
        finally
        {
            _reconcileGate.Release();
        }
    }

    private async Task StartShardAsync(
        ShardKey key,
        IndexMapping mapping,
        bool isSnapshotOwner,
        CancellationToken cancellationToken)
    {
        _logger?.LogInformation("Recovering {Index}/{Shard}.", key.Index, key.ShardId);

        await _controller.MarkShardRecoveringAsync(_nodeId, key.Index, key.ShardId, cancellationToken);

        var runtime = await ShardRuntime.StartAsync(
            _nodeId,
            mapping,
            key.ShardId,
            _analyzers,
            _documents,
            _checkpoints,
            _segments,
            _logger,
            cancellationToken);

        runtime.IsSnapshotOwner = isSnapshotOwner;
        _shards[key] = runtime;

        // Only announced as ready once it has caught up, so no query reaches a half-built replica.
        await _controller.MarkShardStartedAsync(_nodeId, key.Index, key.ShardId, cancellationToken);

        _logger?.LogInformation(
            "{Index}/{Shard} started with {Count} documents.",
            key.Index,
            key.ShardId,
            runtime.Index.DocumentCount);
    }

    /// <summary>Applies pending changes to every shard. Returns how many documents were applied.</summary>
    public async Task<int> CatchUpAllAsync(CancellationToken cancellationToken)
    {
        var total = 0;

        foreach (var shard in _shards.Values)
        {
            total += await shard.CatchUpAsync(cancellationToken);
        }

        return total;
    }

    public async Task SnapshotAllAsync(CancellationToken cancellationToken)
    {
        foreach (var shard in _shards.Values)
        {
            await shard.SnapshotAsync(cancellationToken);
        }
    }

    /// <summary>True once every owned shard is serving. Backs the readiness probe.</summary>
    public bool IsReady => _shards.Values.All(s => s.State == ShardState.Started);

    private readonly record struct ShardKey(string Index, int ShardId);
}
