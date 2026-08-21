using DistSear.Abstractions.Cluster;
using DistSear.Cluster.Allocation;
using Xunit;

namespace DistSear.Cluster.Tests;

public class ShardAllocatorTests
{
    private static readonly string[] ThreeNodes = ["node-a", "node-b", "node-c"];

    [Fact]
    public void AllocatesEveryShard()
    {
        var allocations = ShardAllocator.Allocate("products", numberOfShards: 4, numberOfReplicas: 2, ThreeNodes);

        Assert.Equal(4, allocations.Count);
        Assert.Equal([0, 1, 2, 3], allocations.Select(a => a.ShardId).Order().ToArray());
        Assert.All(allocations, a => Assert.Equal(2, a.Copies.Count));
    }

    [Fact]
    public void NewCopiesStartInitialising()
    {
        var allocations = ShardAllocator.Allocate("products", 2, 2, ThreeNodes);

        Assert.All(allocations.SelectMany(a => a.Copies), c =>
            Assert.Equal(ShardState.Initializing, c.State));
    }

    [Fact]
    public void ExactlyOneCopyPerShardOwnsSnapshots()
    {
        var allocations = ShardAllocator.Allocate("products", 4, 3, ThreeNodes);

        Assert.All(allocations, a => Assert.Single(a.Copies, c => c.IsSnapshotOwner));
    }

    [Fact]
    public void ReconcilingAnUnchangedClusterPreservesStartedState()
    {
        var initial = ShardAllocator.Allocate("products", 4, 2, ThreeNodes);
        var started = Started(initial);

        var reconciled = ShardAllocator.Allocate("products", 4, 2, ThreeNodes, started);

        // A routine reconciliation must not knock a healthy cluster back into recovery.
        Assert.All(reconciled.SelectMany(a => a.Copies), c =>
            Assert.Equal(ShardState.Started, c.State));
    }

    [Fact]
    public void LosingANodeReassignsOnlyItsShards()
    {
        var started = Started(ShardAllocator.Allocate("products", 8, 2, ThreeNodes));
        var survivors = new[] { "node-a", "node-b" };

        var reconciled = ShardAllocator.Allocate("products", 8, 2, survivors, started);

        Assert.DoesNotContain(
            reconciled.SelectMany(a => a.Copies),
            c => c.NodeId == "node-c");

        // Copies that stayed put keep serving; only the replacements need to recover.
        var retained = reconciled.SelectMany(a => a.Copies).Count(c => c.State == ShardState.Started);
        Assert.True(retained > 0, "Every copy was reset, which would take the cluster offline.");
    }

    [Fact]
    public void AddingANodeLeavesExistingCopiesServing()
    {
        var started = Started(ShardAllocator.Allocate("products", 8, 2, ThreeNodes));
        var expanded = ThreeNodes.Append("node-d").ToArray();

        var reconciled = ShardAllocator.Allocate("products", 8, 2, expanded, started);
        var copies = reconciled.SelectMany(a => a.Copies).ToList();

        Assert.Contains(copies, c => c.NodeId == "node-d");
        Assert.Contains(copies, c => c.State == ShardState.Started);
    }

    [Fact]
    public void RequestingMoreReplicasThanNodesAllocatesWhatItCan()
    {
        var allocations = ShardAllocator.Allocate("products", 2, numberOfReplicas: 5, ThreeNodes);

        Assert.All(allocations, a => Assert.Equal(3, a.Copies.Count));
    }

    [Fact]
    public void AnEmptyClusterLeavesEveryShardUnavailable()
    {
        var allocations = ShardAllocator.Allocate("products", 3, 2, []);

        Assert.All(allocations, a => Assert.Empty(a.Copies));
        Assert.Equal(3, ShardAllocator.UnavailableShards(allocations).Count());
    }

    [Fact]
    public void ShardsAreReportedUnavailableUntilACopyStarts()
    {
        var initialising = ShardAllocator.Allocate("products", 3, 2, ThreeNodes);

        Assert.Equal(3, ShardAllocator.UnavailableShards(initialising).Count());
        Assert.Empty(ShardAllocator.UnavailableShards(Started(initialising)));
    }

    [Fact]
    public void ShardsByNodeInvertsTheAllocation()
    {
        var allocations = ShardAllocator.Allocate("products", 6, 2, ThreeNodes);
        var byNode = ShardAllocator.ShardsByNode(allocations);

        Assert.Equal(12, byNode.Values.Sum(s => s.Count));
        Assert.All(byNode.Values, shards => Assert.Equal(shards.Count, shards.Distinct().Count()));
    }

    [Fact]
    public void AllocationIgnoresOtherIndexes()
    {
        var other = ShardAllocator.Allocate("other", 4, 2, ThreeNodes);
        var products = ShardAllocator.Allocate("products", 4, 2, ThreeNodes, other);

        Assert.All(products, a => Assert.Equal("products", a.Index));
    }

    private static IReadOnlyList<ShardAllocation> Started(IReadOnlyList<ShardAllocation> allocations) =>
    [
        .. allocations.Select(a => a with
        {
            Copies = [.. a.Copies.Select(c => c with { State = ShardState.Started })]
        })
    ];
}

public class AdaptiveReplicaSelectorTests
{
    [Fact]
    public void PrefersTheFasterReplica()
    {
        var selector = new AdaptiveReplicaSelector();

        selector.Record("fast", TimeSpan.FromMilliseconds(5));
        selector.Record("slow", TimeSpan.FromMilliseconds(500));

        Assert.Equal("fast", selector.Select(["fast", "slow"]));
    }

    [Fact]
    public void AnUnknownNodeIsTriedRatherThanStarved()
    {
        var selector = new AdaptiveReplicaSelector();
        selector.Record("known", TimeSpan.FromMilliseconds(50));

        // A newly joined replica has no history and must get the chance to prove itself.
        Assert.Equal("fresh", selector.Select(["known", "fresh"]));
    }

    [Fact]
    public void RecoversWhenASlowNodeSpeedsUp()
    {
        var selector = new AdaptiveReplicaSelector();

        selector.Record("a", TimeSpan.FromMilliseconds(500));
        selector.Record("b", TimeSpan.FromMilliseconds(10));
        Assert.Equal("b", selector.Select(["a", "b"]));

        for (var i = 0; i < 20; i++)
        {
            selector.Record("a", TimeSpan.FromMilliseconds(1));
        }

        Assert.Equal("a", selector.Select(["a", "b"]));
    }

    [Fact]
    public void ASingleSlowRequestDoesNotEvictANodeThatIsStillTheBetterChoice()
    {
        var selector = new AdaptiveReplicaSelector();

        // Both nodes have an established history, so neither is carrying the newcomer default.
        for (var i = 0; i < 30; i++)
        {
            selector.Record("steady", TimeSpan.FromMilliseconds(10));
            selector.Record("other", TimeSpan.FromMilliseconds(200));
        }

        selector.Record("steady", TimeSpan.FromMilliseconds(300));

        // Smoothing moves the average rather than replacing it, so one outlier does not overturn a
        // long good record against a genuinely slower peer.
        Assert.Equal("steady", selector.Select(["steady", "other"]));
    }

    [Fact]
    public void ANodeRecoversItsStandingWithinAFewGoodRequests()
    {
        var selector = new AdaptiveReplicaSelector();

        for (var i = 0; i < 30; i++)
        {
            selector.Record("node", TimeSpan.FromMilliseconds(10));
        }

        var baseline = selector.Rank("node");
        selector.Record("node", TimeSpan.FromMilliseconds(300));

        Assert.True(selector.Rank("node") > baseline * 5, "The spike should have been felt.");

        for (var i = 0; i < 10; i++)
        {
            selector.Record("node", TimeSpan.FromMilliseconds(10));
        }

        // Reacting quickly is only useful if it also forgives quickly.
        Assert.True(selector.Rank("node") < baseline * 1.5, "The node never recovered its standing.");
    }

    [Fact]
    public void OutstandingRequestsCountAgainstANode()
    {
        var selector = new AdaptiveReplicaSelector();

        selector.Record("busy", TimeSpan.FromMilliseconds(10));
        selector.Record("idle", TimeSpan.FromMilliseconds(12));

        using var _ = selector.BeginRequest("busy");
        using var __ = selector.BeginRequest("busy");

        // A marginally slower node with an empty queue beats a fast one with work in flight.
        Assert.Equal("idle", selector.Select(["busy", "idle"]));
    }

    [Fact]
    public void FailurePenalisesTheNodeImmediately()
    {
        var selector = new AdaptiveReplicaSelector();

        selector.Record("a", TimeSpan.FromMilliseconds(10));
        selector.Record("b", TimeSpan.FromMilliseconds(11));
        selector.RecordFailure("a");

        Assert.Equal("b", selector.Select(["a", "b"]));
    }

    [Fact]
    public void SingleCandidateIsReturnedWithoutRanking() =>
        Assert.Equal("only", new AdaptiveReplicaSelector().Select(["only"]));

    [Fact]
    public void NoCandidatesReturnsNull() =>
        Assert.Null(new AdaptiveReplicaSelector().Select([]));
}
