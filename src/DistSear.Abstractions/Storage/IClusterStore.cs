using DistSear.Abstractions.Cluster;

namespace DistSear.Abstractions.Storage;

/// <summary>
/// Persistence for the cluster topology. Updates are compare-and-swap on the state's ETag so that
/// two coordinators racing during a leadership handover cannot both commit.
/// </summary>
public interface IClusterStore
{
    Task<ClusterState> GetAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Attempts to persist <paramref name="next"/>, succeeding only if the stored ETag still
    /// matches the one <paramref name="next"/> was derived from. Returns the persisted state with
    /// its new ETag, or null when another writer won the race and the caller should re-read and retry.
    /// </summary>
    Task<ClusterState?> TryUpdateAsync(ClusterState next, CancellationToken cancellationToken);

    /// <summary>Records a node heartbeat. Nodes whose heartbeat goes stale are reallocated by the leader.</summary>
    Task HeartbeatAsync(NodeInfo node, CancellationToken cancellationToken);

    Task<IReadOnlyList<NodeInfo>> GetNodesAsync(CancellationToken cancellationToken);
}

/// <summary>
/// A held leadership lease. <see cref="Lost"/> fires if the lease cannot be renewed, which is the
/// signal for the holder to abandon any coordination work immediately rather than act as a second
/// leader alongside whoever acquired it next.
/// </summary>
public interface ILeaderLease : IAsyncDisposable
{
    CancellationToken Lost { get; }
}

/// <summary>
/// Distributed mutex used to elect a single coordinator. Implemented over Azure Blob leases, which
/// expire on their own if the holder dies, so a crashed leader cannot block the election forever.
/// </summary>
public interface ILeaderElector
{
    /// <summary>Returns a held lease, or null if another instance currently holds it.</summary>
    Task<ILeaderLease?> TryAcquireAsync(string name, CancellationToken cancellationToken);
}
