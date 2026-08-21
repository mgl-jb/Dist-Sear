namespace DistSear.Node.Hosting;

public sealed class NodeOptions
{
    public const string SectionName = "DistSear:Node";

    /// <summary>
    /// Stable identity for this replica. Shard placement is a function of the node id, so a replica
    /// that comes back with a different id is treated as a new node and is allocated different
    /// shards. Container Apps supplies a per-replica hostname, which is used when nothing is set.
    /// </summary>
    public string NodeId { get; set; } = Environment.GetEnvironmentVariable("CONTAINER_APP_REPLICA_NAME")
        ?? Environment.MachineName;

    /// <summary>Address the coordinator reaches this node on, over internal ingress.</summary>
    public string Address { get; set; } = "http://localhost:8080";

    /// <summary>How often the node announces it is alive. Must be well under the store's timeout.</summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How often the node re-reads the allocation table and converges on it.</summary>
    public TimeSpan ReconcileInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How often buffered writes become searchable. The central near-real-time trade-off: shorter
    /// means fresher results, longer means fewer segments and less merging.
    /// </summary>
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>How often the nominated copy uploads a snapshot, bounding recovery replay.</summary>
    public TimeSpan SnapshotInterval { get; set; } = TimeSpan.FromMinutes(5);
}
