using System.Collections.Concurrent;
using System.Globalization;
using DistSear.Abstractions.Cluster;
using DistSear.Abstractions.Storage;

namespace DistSear.Cluster.InMemory;

/// <summary>
/// In-process cluster store with the same compare-and-swap contract as the Cosmos-backed one, so
/// tests exercise the real concurrency behaviour: a writer holding a stale ETag is rejected and has
/// to re-read, exactly as it would in production.
/// </summary>
public sealed class InMemoryClusterStore : IClusterStore
{
    private readonly Lock _gate = new();
    private readonly ConcurrentDictionary<string, NodeInfo> _nodes = new(StringComparer.Ordinal);

    private ClusterState _state = new() { ETag = "0" };
    private long _version;

    /// <summary>How long a node may go without a heartbeat before it is treated as gone.</summary>
    public TimeSpan HeartbeatTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>Rejected updates, for asserting that contention was genuinely exercised.</summary>
    public int ConflictCount { get; private set; }

    public Task<ClusterState> GetAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(_state with { Nodes = LiveNodes() });
        }
    }

    public Task<ClusterState?> TryUpdateAsync(ClusterState next, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!string.Equals(next.ETag, _state.ETag, StringComparison.Ordinal))
            {
                ConflictCount++;
                return Task.FromResult<ClusterState?>(null);
            }

            _state = next with { ETag = (++_version).ToString(CultureInfo.InvariantCulture) };
            return Task.FromResult<ClusterState?>(_state with { Nodes = LiveNodes() });
        }
    }

    public Task HeartbeatAsync(NodeInfo node, CancellationToken cancellationToken)
    {
        _nodes[node.NodeId] = node with { LastHeartbeat = TimeProvider.GetUtcNow() };
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<NodeInfo>> GetNodesAsync(CancellationToken cancellationToken) =>
        Task.FromResult(LiveNodes());

    /// <summary>Removes a node immediately, simulating a crash rather than a graceful shutdown.</summary>
    public void Kill(string nodeId) => _nodes.TryRemove(nodeId, out _);

    private IReadOnlyList<NodeInfo> LiveNodes()
    {
        var cutoff = TimeProvider.GetUtcNow() - HeartbeatTimeout;

        return
        [
            .. _nodes.Values
                .Where(n => n.LastHeartbeat >= cutoff)
                .OrderBy(n => n.NodeId, StringComparer.Ordinal)
        ];
    }
}
