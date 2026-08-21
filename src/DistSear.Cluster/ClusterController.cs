using DistSear.Abstractions.Cluster;
using DistSear.Abstractions.Mapping;
using DistSear.Abstractions.Storage;
using DistSear.Cluster.Allocation;

namespace DistSear.Cluster;

/// <summary>
/// Maintains the cluster's shard allocation table.
///
/// Only the instance holding the leadership lease reconciles, so two coordinators cannot assign the
/// same shard differently. Leadership alone is not relied on for safety though: every write is a
/// compare-and-swap on the state's ETag, so even a split brain during a handover produces a
/// rejected write rather than a lost update.
/// </summary>
public sealed class ClusterController
{
    /// <summary>Name of the lease that grants the right to reconcile.</summary>
    public const string AllocationLeaseName = "shard-allocation";

    /// <summary>
    /// Attempts before giving up on a contended write. Conflicts mean another writer succeeded, so
    /// retrying re-reads and re-plans rather than repeating a stale decision.
    /// </summary>
    private const int MaxConflictRetries = 5;

    private readonly IClusterStore _store;
    private readonly ILeaderElector _elector;

    public ClusterController(IClusterStore store, ILeaderElector elector)
    {
        _store = store;
        _elector = elector;
    }

    public Task<ClusterState> GetStateAsync(CancellationToken cancellationToken) =>
        _store.GetAsync(cancellationToken);

    /// <summary>
    /// Reconciles the allocation table against the live node set, if this instance can take
    /// leadership. Returns false when another instance is leading, which is not an error.
    /// </summary>
    public async Task<bool> TryReconcileAsync(CancellationToken cancellationToken)
    {
        await using var lease = await _elector.TryAcquireAsync(AllocationLeaseName, cancellationToken);

        if (lease is null)
        {
            return false;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lease.Lost);

        await MutateAsync(
            state =>
            {
                var liveNodes = state.Nodes.Select(n => n.NodeId).ToArray();
                var shards = new List<ShardAllocation>();

                foreach (var (name, metadata) in state.Indexes)
                {
                    shards.AddRange(ShardAllocator.Allocate(
                        name,
                        metadata.Mapping.NumberOfShards,
                        metadata.Mapping.NumberOfReplicas,
                        liveNodes,
                        state.Shards));
                }

                return AllocationsEqual(state.Shards, shards) ? null : state with { Shards = shards };
            },
            linked.Token);

        return true;
    }

    /// <summary>Creates an index and allocates its shards across the live nodes.</summary>
    public async Task CreateIndexAsync(IndexMapping mapping, CancellationToken cancellationToken)
    {
        await MutateAsync(
            state =>
            {
                if (state.Indexes.ContainsKey(mapping.Name))
                {
                    throw new InvalidOperationException($"Index '{mapping.Name}' already exists.");
                }

                var indexes = new Dictionary<string, IndexMetadata>(state.Indexes, StringComparer.Ordinal)
                {
                    [mapping.Name] = new()
                    {
                        Mapping = mapping,
                        CreatedAt = DateTimeOffset.UtcNow
                    }
                };

                var liveNodes = state.Nodes.Select(n => n.NodeId).ToArray();

                var shards = state.Shards
                    .Concat(ShardAllocator.Allocate(
                        mapping.Name,
                        mapping.NumberOfShards,
                        mapping.NumberOfReplicas,
                        liveNodes))
                    .ToList();

                return state with { Indexes = indexes, Shards = shards };
            },
            cancellationToken);
    }

    public async Task DeleteIndexAsync(string name, CancellationToken cancellationToken)
    {
        await MutateAsync(
            state =>
            {
                if (!state.Indexes.ContainsKey(name))
                {
                    return null;
                }

                var indexes = new Dictionary<string, IndexMetadata>(state.Indexes, StringComparer.Ordinal);
                indexes.Remove(name);

                // Aliases pointing at a deleted index would resolve to nothing, so drop them too.
                var aliases = state.Aliases
                    .Where(kv => !string.Equals(kv.Value, name, StringComparison.Ordinal))
                    .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

                return state with
                {
                    Indexes = indexes,
                    Aliases = aliases,
                    Shards = [.. state.Shards.Where(s => !string.Equals(s.Index, name, StringComparison.Ordinal))]
                };
            },
            cancellationToken);
    }

    /// <summary>
    /// Points an alias at an index in a single atomic write. This is what makes a zero-downtime
    /// reindex possible: build <c>products_v2</c> alongside <c>products_v1</c>, then swap, and no
    /// query ever observes a half-built index.
    /// </summary>
    public async Task SetAliasAsync(string alias, string index, CancellationToken cancellationToken)
    {
        await MutateAsync(
            state =>
            {
                if (!state.Indexes.ContainsKey(index))
                {
                    throw new InvalidOperationException($"Cannot alias '{alias}' to unknown index '{index}'.");
                }

                if (state.Indexes.ContainsKey(alias))
                {
                    throw new InvalidOperationException(
                        $"'{alias}' is already a concrete index, so it cannot also be an alias.");
                }

                var aliases = new Dictionary<string, string>(state.Aliases, StringComparer.Ordinal)
                {
                    [alias] = index
                };

                return state with { Aliases = aliases };
            },
            cancellationToken);
    }

    /// <summary>Records that a node has finished recovering a shard and can now serve queries.</summary>
    public Task MarkShardStartedAsync(
        string nodeId,
        string index,
        int shardId,
        CancellationToken cancellationToken) =>
        SetShardStateAsync(nodeId, index, shardId, ShardState.Started, cancellationToken);

    public Task MarkShardRecoveringAsync(
        string nodeId,
        string index,
        int shardId,
        CancellationToken cancellationToken) =>
        SetShardStateAsync(nodeId, index, shardId, ShardState.Recovering, cancellationToken);

    private async Task SetShardStateAsync(
        string nodeId,
        string index,
        int shardId,
        ShardState desired,
        CancellationToken cancellationToken)
    {
        await MutateAsync(
            state =>
            {
                var shards = state.Shards.ToList();
                var position = shards.FindIndex(s =>
                    string.Equals(s.Index, index, StringComparison.Ordinal) && s.ShardId == shardId);

                if (position < 0)
                {
                    return null;
                }

                var allocation = shards[position];
                var copies = allocation.Copies.ToList();
                var copyIndex = copies.FindIndex(c => string.Equals(c.NodeId, nodeId, StringComparison.Ordinal));

                // The node may have been reallocated away while it was recovering.
                if (copyIndex < 0 || copies[copyIndex].State == desired)
                {
                    return null;
                }

                copies[copyIndex] = copies[copyIndex] with { State = desired };
                shards[position] = allocation with { Copies = copies };

                return state with { Shards = shards };
            },
            cancellationToken);
    }

    /// <summary>
    /// Applies a change under optimistic concurrency, re-reading and re-applying when another
    /// writer wins the race. The mutator returns null when there is nothing to change.
    /// </summary>
    private async Task MutateAsync(
        Func<ClusterState, ClusterState?> mutate,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaxConflictRetries; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var current = await _store.GetAsync(cancellationToken);
            var next = mutate(current);

            if (next is null)
            {
                return;
            }

            if (await _store.TryUpdateAsync(next, cancellationToken) is not null)
            {
                return;
            }
        }

        throw new InvalidOperationException(
            $"Cluster state could not be updated after {MaxConflictRetries} attempts because of write contention.");
    }

    private static bool AllocationsEqual(
        IReadOnlyList<ShardAllocation> a,
        IReadOnlyList<ShardAllocation> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        var left = a.OrderBy(s => s.Index, StringComparer.Ordinal).ThenBy(s => s.ShardId).ToList();
        var right = b.OrderBy(s => s.Index, StringComparer.Ordinal).ThenBy(s => s.ShardId).ToList();

        for (var i = 0; i < left.Count; i++)
        {
            if (!string.Equals(left[i].Index, right[i].Index, StringComparison.Ordinal)
                || left[i].ShardId != right[i].ShardId
                || !left[i].Copies.SequenceEqual(right[i].Copies))
            {
                return false;
            }
        }

        return true;
    }
}
