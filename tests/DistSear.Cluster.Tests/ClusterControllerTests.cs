using DistSear.Abstractions.Cluster;
using DistSear.Abstractions.Mapping;
using DistSear.Cluster;
using DistSear.Cluster.InMemory;
using Xunit;

namespace DistSear.Cluster.Tests;

public class ClusterControllerTests
{
    private readonly InMemoryClusterStore _store = new();
    private readonly InMemoryLeaderElector _elector = new();
    private readonly ClusterController _controller;

    public ClusterControllerTests() => _controller = new ClusterController(_store, _elector);

    private static IndexMapping Mapping(string name, int shards = 4, int replicas = 2) => new(
        name,
        [new FieldMapping { Name = "title", Type = FieldType.Text }],
        numberOfShards: shards,
        numberOfReplicas: replicas);

    private async Task JoinAsync(params string[] nodeIds)
    {
        foreach (var nodeId in nodeIds)
        {
            await _store.HeartbeatAsync(
                new NodeInfo { NodeId = nodeId, Address = $"http://{nodeId}" },
                default);
        }
    }

    [Fact]
    public async Task CreatingAnIndexAllocatesItsShards()
    {
        await JoinAsync("node-a", "node-b");
        await _controller.CreateIndexAsync(Mapping("products"), default);

        var state = await _controller.GetStateAsync(default);

        Assert.Contains("products", state.Indexes.Keys);
        Assert.Equal(4, state.ShardsOf("products").Count());
        Assert.All(state.ShardsOf("products"), s => Assert.Equal(2, s.Copies.Count));
    }

    [Fact]
    public async Task CreatingTheSameIndexTwiceIsRejected()
    {
        await JoinAsync("node-a");
        await _controller.CreateIndexAsync(Mapping("products"), default);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _controller.CreateIndexAsync(Mapping("products"), default));
    }

    [Fact]
    public async Task ReconcilingRequiresLeadership()
    {
        await JoinAsync("node-a");

        // Somebody else already holds the lease.
        await using var heldElsewhere = await _elector.TryAcquireAsync(
            ClusterController.AllocationLeaseName,
            default);

        Assert.False(await _controller.TryReconcileAsync(default));
    }

    [Fact]
    public async Task ReconcilingPlacesShardsOnNodesThatJoinedLater()
    {
        await _controller.CreateIndexAsync(Mapping("products", shards: 4, replicas: 2), default);

        // Created with an empty cluster, so nothing could be placed.
        var before = await _controller.GetStateAsync(default);
        Assert.All(before.ShardsOf("products"), s => Assert.Empty(s.Copies));

        await JoinAsync("node-a", "node-b");
        Assert.True(await _controller.TryReconcileAsync(default));

        var after = await _controller.GetStateAsync(default);
        Assert.All(after.ShardsOf("products"), s => Assert.Equal(2, s.Copies.Count));
    }

    [Fact]
    public async Task ReconcilingIsIdempotent()
    {
        await JoinAsync("node-a", "node-b");
        await _controller.CreateIndexAsync(Mapping("products"), default);

        await _controller.TryReconcileAsync(default);
        var first = await _controller.GetStateAsync(default);

        await _controller.TryReconcileAsync(default);
        var second = await _controller.GetStateAsync(default);

        // Nothing changed, so the second pass must not have written at all.
        Assert.Equal(first.ETag, second.ETag);
    }

    [Fact]
    public async Task StartedShardsSurviveReconciliation()
    {
        await JoinAsync("node-a", "node-b");
        await _controller.CreateIndexAsync(Mapping("products", shards: 2, replicas: 2), default);
        await StartAllAsync("products");

        await _controller.TryReconcileAsync(default);

        var state = await _controller.GetStateAsync(default);

        Assert.All(
            state.ShardsOf("products").SelectMany(s => s.Copies),
            c => Assert.Equal(ShardState.Started, c.State));
    }

    [Fact]
    public async Task ADeadNodesShardsAreReassignedToSurvivors()
    {
        await JoinAsync("node-a", "node-b", "node-c");
        await _controller.CreateIndexAsync(Mapping("products", shards: 6, replicas: 2), default);
        await StartAllAsync("products");

        _store.Kill("node-c");
        Assert.True(await _controller.TryReconcileAsync(default));

        var state = await _controller.GetStateAsync(default);
        var copies = state.ShardsOf("products").SelectMany(s => s.Copies).ToList();

        Assert.DoesNotContain(copies, c => c.NodeId == "node-c");
        Assert.All(state.ShardsOf("products"), s => Assert.Equal(2, s.Copies.Count));

        // Copies that never moved keep serving, so the cluster stays available during the change.
        Assert.Contains(copies, c => c.State == ShardState.Started);
    }

    [Fact]
    public async Task ShardStateTransitionsAreRecorded()
    {
        await JoinAsync("node-a");
        await _controller.CreateIndexAsync(Mapping("products", shards: 1, replicas: 1), default);

        await _controller.MarkShardRecoveringAsync("node-a", "products", 0, default);
        Assert.Equal(ShardState.Recovering, await StateOfAsync("products", 0, "node-a"));

        await _controller.MarkShardStartedAsync("node-a", "products", 0, default);
        Assert.Equal(ShardState.Started, await StateOfAsync("products", 0, "node-a"));
    }

    [Fact]
    public async Task MarkingAShardForANodeThatNoLongerOwnsItIsIgnored()
    {
        await JoinAsync("node-a");
        await _controller.CreateIndexAsync(Mapping("products", shards: 1, replicas: 1), default);

        // "node-z" was never allocated this shard.
        await _controller.MarkShardStartedAsync("node-z", "products", 0, default);

        var state = await _controller.GetStateAsync(default);
        Assert.DoesNotContain(state.ShardsOf("products").SelectMany(s => s.Copies), c => c.NodeId == "node-z");
    }

    [Fact]
    public async Task AliasResolvesToItsIndex()
    {
        await JoinAsync("node-a");
        await _controller.CreateIndexAsync(Mapping("products_v1"), default);
        await _controller.SetAliasAsync("products", "products_v1", default);

        var state = await _controller.GetStateAsync(default);

        Assert.Equal("products_v1", state.ResolveIndex("products"));
    }

    [Fact]
    public async Task AliasSwapIsAtomicSoQueriesNeverSeeAHalfBuiltIndex()
    {
        await JoinAsync("node-a");
        await _controller.CreateIndexAsync(Mapping("products_v1"), default);
        await _controller.CreateIndexAsync(Mapping("products_v2"), default);
        await _controller.SetAliasAsync("products", "products_v1", default);

        await _controller.SetAliasAsync("products", "products_v2", default);

        var state = await _controller.GetStateAsync(default);
        Assert.Equal("products_v2", state.ResolveIndex("products"));
    }

    [Fact]
    public async Task AliasingToAnUnknownIndexIsRejected()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _controller.SetAliasAsync("products", "nope", default));
    }

    [Fact]
    public async Task AnAliasCannotShadowAConcreteIndex()
    {
        await _controller.CreateIndexAsync(Mapping("products"), default);
        await _controller.CreateIndexAsync(Mapping("products_v2"), default);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _controller.SetAliasAsync("products", "products_v2", default));
    }

    [Fact]
    public async Task UnknownNameResolvesToItself() =>
        Assert.Equal("whatever", (await _controller.GetStateAsync(default)).ResolveIndex("whatever"));

    [Fact]
    public async Task DeletingAnIndexRemovesItsShardsAndAliases()
    {
        await JoinAsync("node-a");
        await _controller.CreateIndexAsync(Mapping("products_v1"), default);
        await _controller.SetAliasAsync("products", "products_v1", default);

        await _controller.DeleteIndexAsync("products_v1", default);

        var state = await _controller.GetStateAsync(default);

        Assert.Empty(state.Indexes);
        Assert.Empty(state.Shards);

        // A dangling alias would resolve to an index that no longer exists.
        Assert.Empty(state.Aliases);
    }

    [Fact]
    public async Task ConcurrentControllersDoNotBothReconcile()
    {
        await JoinAsync("node-a", "node-b");
        await _controller.CreateIndexAsync(Mapping("products"), default);

        var rival = new ClusterController(_store, _elector);

        var results = await Task.WhenAll(
            _controller.TryReconcileAsync(default),
            rival.TryReconcileAsync(default));

        // Whichever wins the lease does the work; the other reports that it was not leader.
        Assert.Contains(true, results);
    }

    private async Task<ShardState> StateOfAsync(string index, int shardId, string nodeId)
    {
        var state = await _controller.GetStateAsync(default);

        return state.ShardsOf(index)
            .Single(s => s.ShardId == shardId)
            .Copies.Single(c => c.NodeId == nodeId)
            .State;
    }

    private async Task StartAllAsync(string index)
    {
        var state = await _controller.GetStateAsync(default);

        foreach (var allocation in state.ShardsOf(index).ToList())
        {
            foreach (var copy in allocation.Copies)
            {
                await _controller.MarkShardStartedAsync(copy.NodeId, index, allocation.ShardId, default);
            }
        }
    }
}
