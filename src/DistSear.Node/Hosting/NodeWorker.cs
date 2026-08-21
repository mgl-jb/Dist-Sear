using DistSear.Abstractions.Cluster;
using DistSear.Abstractions.Storage;
using DistSear.Cluster;
using DistSear.Node.Shards;
using Microsoft.Extensions.Options;

namespace DistSear.Node.Hosting;

/// <summary>
/// The node's background loop: heartbeat, converge on the allocation table, apply new changes, and
/// snapshot on a slower cadence.
///
/// Every step is individually guarded. A node that cannot reach the cluster store must keep serving
/// queries from the index it already holds rather than falling over — losing the control plane
/// should degrade the cluster's ability to change, not its ability to answer.
/// </summary>
public sealed class NodeWorker : BackgroundService
{
    private readonly ShardHost _host;
    private readonly ClusterController _controller;
    private readonly IClusterStore _clusterStore;
    private readonly NodeOptions _options;
    private readonly ILogger<NodeWorker> _logger;
    private readonly TimeProvider _time;

    private DateTimeOffset _lastReconcile = DateTimeOffset.MinValue;
    private DateTimeOffset _lastSnapshot = DateTimeOffset.MinValue;

    public NodeWorker(
        ShardHost host,
        ClusterController controller,
        IClusterStore clusterStore,
        IOptions<NodeOptions> options,
        ILogger<NodeWorker> logger,
        TimeProvider? time = null)
    {
        _host = host;
        _controller = controller;
        _clusterStore = clusterStore;
        _options = options.Value;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Node {NodeId} starting at {Address}.", _options.NodeId, _options.Address);

        while (!stoppingToken.IsCancellationRequested)
        {
            await RunCycleAsync(stoppingToken);

            try
            {
                await Task.Delay(_options.HeartbeatInterval, _time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>One pass of the loop. Public so tests can drive it deterministically.</summary>
    public async Task RunCycleAsync(CancellationToken cancellationToken)
    {
        await SafelyAsync(HeartbeatAsync, "heartbeat", cancellationToken);
        await SafelyAsync(MaybeReconcileAsync, "reconcile", cancellationToken);
        await SafelyAsync(CatchUpAsync, "catch-up", cancellationToken);
        await SafelyAsync(MaybeSnapshotAsync, "snapshot", cancellationToken);
    }

    private Task HeartbeatAsync(CancellationToken cancellationToken) =>
        _clusterStore.HeartbeatAsync(
            new NodeInfo
            {
                NodeId = _options.NodeId,
                Address = _options.Address,
                LastHeartbeat = _time.GetUtcNow(),
                Status = NodeStatus.Alive
            },
            cancellationToken);

    private async Task MaybeReconcileAsync(CancellationToken cancellationToken)
    {
        if (_time.GetUtcNow() - _lastReconcile < _options.ReconcileInterval)
        {
            return;
        }

        _lastReconcile = _time.GetUtcNow();

        // Any node may attempt to lead; the lease decides. Reconciling is the leader's job, but
        // converging on the published table is every node's job.
        await _controller.TryReconcileAsync(cancellationToken);

        var state = await _controller.GetStateAsync(cancellationToken);
        await _host.ReconcileAsync(state, cancellationToken);
    }

    private async Task CatchUpAsync(CancellationToken cancellationToken) =>
        await _host.CatchUpAllAsync(cancellationToken);

    private async Task MaybeSnapshotAsync(CancellationToken cancellationToken)
    {
        if (_time.GetUtcNow() - _lastSnapshot < _options.SnapshotInterval)
        {
            return;
        }

        _lastSnapshot = _time.GetUtcNow();
        await _host.SnapshotAllAsync(cancellationToken);
    }

    private async Task SafelyAsync(
        Func<CancellationToken, Task> step,
        string name,
        CancellationToken cancellationToken)
    {
        try
        {
            await step(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Deliberately swallowed: one failing step must not stop the loop, or a transient
            // control-plane outage would permanently stall indexing on this node.
            _logger.LogError(exception, "Node cycle step '{Step}' failed.", name);
        }
    }
}
