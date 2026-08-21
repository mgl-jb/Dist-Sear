using DistSear.Abstractions.Mapping;

namespace DistSear.Abstractions.Cluster;

public enum ShardState
{
    /// <summary>No node owns this shard. Queries against it fail and are reported in shard stats.</summary>
    Unassigned,

    /// <summary>A node has claimed the shard but has not begun restoring it.</summary>
    Initializing,

    /// <summary>Restoring from the latest Blob snapshot and replaying the change feed from its token.</summary>
    Recovering,

    /// <summary>Caught up and serving queries.</summary>
    Started
}

public enum NodeStatus
{
    Alive,
    Suspect,
    Dead
}

public sealed record NodeInfo
{
    public required string NodeId { get; init; }

    /// <summary>Base address used by the coordinator for internal shard calls.</summary>
    public required string Address { get; init; }

    public DateTimeOffset LastHeartbeat { get; init; }

    public NodeStatus Status { get; init; } = NodeStatus.Alive;
}

/// <summary>
/// One copy of a shard on one node. Because Cosmos DB is the source of truth and every copy
/// re-derives its index independently from the change feed, copies are symmetric for querying —
/// there is no write-path primary. <see cref="IsSnapshotOwner"/> designates the single copy
/// responsible for uploading segment snapshots, so N replicas do not each upload the same bytes.
/// </summary>
public sealed record ShardCopy
{
    public required string NodeId { get; init; }

    public ShardState State { get; init; } = ShardState.Initializing;

    public bool IsSnapshotOwner { get; init; }
}

public sealed record ShardAllocation
{
    public required string Index { get; init; }

    public required int ShardId { get; init; }

    public IReadOnlyList<ShardCopy> Copies { get; init; } = [];

    public IEnumerable<ShardCopy> Searchable => Copies.Where(c => c.State == ShardState.Started);
}

public sealed record IndexMetadata
{
    public required IndexMapping Mapping { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Bumped on every refresh; used to invalidate cached query results for this index.</summary>
    public long Generation { get; init; }
}

/// <summary>
/// The authoritative cluster topology. Mutated only by the elected leader, under optimistic
/// concurrency on <see cref="ETag"/>.
/// </summary>
public sealed record ClusterState
{
    public IReadOnlyDictionary<string, IndexMetadata> Indexes { get; init; } =
        new Dictionary<string, IndexMetadata>();

    /// <summary>Alias name to concrete index name. Enables zero-downtime reindex via atomic swap.</summary>
    public IReadOnlyDictionary<string, string> Aliases { get; init; } =
        new Dictionary<string, string>();

    public IReadOnlyList<NodeInfo> Nodes { get; init; } = [];

    public IReadOnlyList<ShardAllocation> Shards { get; init; } = [];

    /// <summary>Opaque concurrency token from the backing store; null for a state never persisted.</summary>
    public string? ETag { get; init; }

    /// <summary>Resolves an alias to a concrete index name, or returns the input unchanged.</summary>
    public string ResolveIndex(string nameOrAlias) =>
        Aliases.TryGetValue(nameOrAlias, out var target) ? target : nameOrAlias;

    public IEnumerable<ShardAllocation> ShardsOf(string index) =>
        Shards.Where(s => string.Equals(s.Index, index, StringComparison.Ordinal));
}
