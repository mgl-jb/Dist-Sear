using DistSear.Abstractions.Cluster;
using DistSear.Cluster.Routing;

namespace DistSear.Cluster.Allocation;

/// <summary>
/// Computes where each shard of an index should live.
///
/// Placement is derived from rendezvous hashing over the live node set, so it is deterministic:
/// any coordinator, at any time, given the same nodes, produces the same plan. Existing copies are
/// carried forward with their state intact — a shard that is already <see cref="ShardState.Started"/>
/// on a node that still owns it must not be knocked back to initialising, or a routine
/// reconciliation would take a healthy cluster offline.
/// </summary>
public static class ShardAllocator
{
    public static IReadOnlyList<ShardAllocation> Allocate(
        string index,
        int numberOfShards,
        int numberOfReplicas,
        IReadOnlyCollection<string> liveNodeIds,
        IReadOnlyList<ShardAllocation>? existing = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(numberOfShards, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(numberOfReplicas, 1);

        var current = (existing ?? [])
            .Where(a => string.Equals(a.Index, index, StringComparison.Ordinal))
            .ToDictionary(a => a.ShardId);

        var allocations = new List<ShardAllocation>(numberOfShards);

        for (var shardId = 0; shardId < numberOfShards; shardId++)
        {
            var shardKey = DocumentRouter.ShardKey(shardId);
            var desired = RendezvousHash.Select(shardKey, liveNodeIds, numberOfReplicas);

            current.TryGetValue(shardId, out var previous);

            var copies = new List<ShardCopy>(desired.Count);

            foreach (var nodeId in desired)
            {
                var retained = previous?.Copies.FirstOrDefault(c =>
                    string.Equals(c.NodeId, nodeId, StringComparison.Ordinal));

                copies.Add(new ShardCopy
                {
                    NodeId = nodeId,

                    // A node keeps whatever progress it had; a newly assigned one starts fresh.
                    State = retained?.State ?? ShardState.Initializing,
                    IsSnapshotOwner = false
                });
            }

            allocations.Add(new ShardAllocation
            {
                Index = index,
                ShardId = shardId,
                Copies = AssignSnapshotOwner(copies)
            });
        }

        return allocations;
    }

    /// <summary>
    /// Nominates exactly one copy to upload snapshots, preferring a copy that is already serving.
    /// Without this every replica would upload identical segment bytes to Blob Storage.
    /// </summary>
    private static IReadOnlyList<ShardCopy> AssignSnapshotOwner(List<ShardCopy> copies)
    {
        if (copies.Count == 0)
        {
            return copies;
        }

        var ownerIndex = copies.FindIndex(c => c.State == ShardState.Started);

        if (ownerIndex < 0)
        {
            ownerIndex = 0;
        }

        for (var i = 0; i < copies.Count; i++)
        {
            copies[i] = copies[i] with { IsSnapshotOwner = i == ownerIndex };
        }

        return copies;
    }

    /// <summary>
    /// Shards that have no copy able to serve. A query touching one of these reports a shard
    /// failure rather than silently returning fewer results.
    /// </summary>
    public static IEnumerable<ShardAllocation> UnavailableShards(IEnumerable<ShardAllocation> allocations) =>
        allocations.Where(a => !a.Searchable.Any());

    /// <summary>Shards each node owns, for driving that node's change-feed readers.</summary>
    public static IReadOnlyDictionary<string, List<int>> ShardsByNode(
        IEnumerable<ShardAllocation> allocations)
    {
        var byNode = new Dictionary<string, List<int>>(StringComparer.Ordinal);

        foreach (var allocation in allocations)
        {
            foreach (var copy in allocation.Copies)
            {
                if (!byNode.TryGetValue(copy.NodeId, out var shards))
                {
                    shards = [];
                    byNode[copy.NodeId] = shards;
                }

                shards.Add(allocation.ShardId);
            }
        }

        return byNode;
    }
}
