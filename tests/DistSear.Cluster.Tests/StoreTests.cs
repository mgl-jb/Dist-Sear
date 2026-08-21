using DistSear.Abstractions.Cluster;
using DistSear.Abstractions.Documents;
using DistSear.Abstractions.Storage;
using DistSear.Cluster.InMemory;
using Xunit;

namespace DistSear.Cluster.Tests;

public class DocumentStoreTests
{
    private readonly InMemoryDocumentStore _store = new() { BatchSize = 3 };

    private static IndexedDocument Doc(string id, string shardKey, bool deleted = false) => new()
    {
        Id = id,
        ShardKey = shardKey,
        Deleted = deleted,
        Fields = new Dictionary<string, object?> { ["title"] = "doc " + id }
    };

    [Fact]
    public async Task ChangeFeedReturnsWhatWasWritten()
    {
        await _store.UpsertAsync([Doc("1", "shard-0"), Doc("2", "shard-0")], default);

        await using var cursor = _store.OpenChangeFeed("shard-0", null);
        var batch = await cursor.ReadNextAsync(default);

        Assert.Equal(["1", "2"], batch.Documents.Select(d => d.Id));
    }

    [Fact]
    public async Task ChangeFeedIsScopedToOneShard()
    {
        await _store.UpsertAsync([Doc("1", "shard-0"), Doc("2", "shard-1")], default);

        await using var cursor = _store.OpenChangeFeed("shard-0", null);
        var batch = await cursor.ReadNextAsync(default);

        // A node must never pay to read documents belonging to shards it does not own.
        Assert.Equal(["1"], batch.Documents.Select(d => d.Id));
    }

    [Fact]
    public async Task ContinuationTokenResumesExactlyWhereReadingStopped()
    {
        await _store.UpsertAsync([.. Enumerable.Range(1, 7).Select(i => Doc(i.ToString(), "shard-0"))], default);

        await using var first = _store.OpenChangeFeed("shard-0", null);
        var batch = await first.ReadNextAsync(default);

        Assert.Equal(3, batch.Documents.Count);
        Assert.True(batch.HasMore);

        // A fresh cursor built from the token must continue, not restart.
        await using var resumed = _store.OpenChangeFeed("shard-0", batch.ContinuationToken);
        var next = await resumed.ReadNextAsync(default);

        Assert.Equal(["4", "5", "6"], next.Documents.Select(d => d.Id));
    }

    [Fact]
    public async Task ReplayingFromAnOlderTokenRedeliversDocuments()
    {
        await _store.UpsertAsync([Doc("1", "shard-0"), Doc("2", "shard-0")], default);

        await using var cursor = _store.OpenChangeFeed("shard-0", "0");
        var batch = await cursor.ReadNextAsync(default);

        // At-least-once delivery is the contract; indexing is idempotent so replay is safe.
        Assert.Equal(2, batch.Documents.Count);
    }

    [Fact]
    public async Task FeedReportsWhenItIsDrained()
    {
        await _store.UpsertAsync([Doc("1", "shard-0")], default);

        await using var cursor = _store.OpenChangeFeed("shard-0", null);
        await cursor.ReadNextAsync(default);

        var empty = await cursor.ReadNextAsync(default);

        Assert.Empty(empty.Documents);
        Assert.False(empty.HasMore);
    }

    [Fact]
    public async Task UpdatesAppearOnTheFeedAsNewEntries()
    {
        await _store.UpsertAsync([Doc("1", "shard-0")], default);
        await _store.UpsertAsync([Doc("1", "shard-0")], default);

        await using var cursor = _store.OpenChangeFeed("shard-0", null);
        var batch = await cursor.ReadNextAsync(default);

        Assert.Equal(2, batch.Documents.Count);
        Assert.True(batch.Documents[1].Version > batch.Documents[0].Version);
    }

    [Fact]
    public async Task SoftDeletesTravelOnTheFeed()
    {
        await _store.UpsertAsync([Doc("1", "shard-0")], default);
        await _store.UpsertAsync([Doc("1", "shard-0", deleted: true)], default);

        await using var cursor = _store.OpenChangeFeed("shard-0", null);
        var batch = await cursor.ReadNextAsync(default);

        // This is why deletes are soft: a hard delete would simply vanish from the feed and
        // replicas would never learn the document is gone.
        Assert.True(batch.Documents[^1].Deleted);
    }

    [Fact]
    public async Task PointReadsSkipDeletedDocuments()
    {
        await _store.UpsertAsync([Doc("1", "shard-0"), Doc("2", "shard-0", deleted: true)], default);

        var found = await _store.GetAsync("shard-0", ["1", "2"], default);

        Assert.Equal(["1"], found.Select(d => d.Id));
    }
}

public class ClusterStoreTests
{
    private readonly InMemoryClusterStore _store = new();

    [Fact]
    public async Task UpdateSucceedsWithTheCurrentETag()
    {
        var state = await _store.GetAsync(default);
        var updated = await _store.TryUpdateAsync(
            state with { Aliases = new Dictionary<string, string> { ["live"] = "products_v1" } },
            default);

        Assert.NotNull(updated);
        Assert.Equal("products_v1", updated.ResolveIndex("live"));
    }

    [Fact]
    public async Task UpdateWithAStaleETagIsRejected()
    {
        var stale = await _store.GetAsync(default);

        await _store.TryUpdateAsync(stale with { Aliases = new Dictionary<string, string> { ["a"] = "1" } }, default);

        // The second writer is working from a version that no longer exists and must lose.
        var loser = await _store.TryUpdateAsync(
            stale with { Aliases = new Dictionary<string, string> { ["b"] = "2" } },
            default);

        Assert.Null(loser);
        Assert.Equal(1, _store.ConflictCount);
    }

    [Fact]
    public async Task RereadingAfterAConflictAllowsTheWriteToSucceed()
    {
        var stale = await _store.GetAsync(default);
        await _store.TryUpdateAsync(stale with { Aliases = new Dictionary<string, string> { ["a"] = "1" } }, default);

        var fresh = await _store.GetAsync(default);
        var retried = await _store.TryUpdateAsync(
            fresh with { Aliases = new Dictionary<string, string> { ["b"] = "2" } },
            default);

        Assert.NotNull(retried);
    }

    [Fact]
    public async Task HeartbeatsRegisterLiveNodes()
    {
        await _store.HeartbeatAsync(new NodeInfo { NodeId = "node-a", Address = "http://a" }, default);
        await _store.HeartbeatAsync(new NodeInfo { NodeId = "node-b", Address = "http://b" }, default);

        var nodes = await _store.GetNodesAsync(default);

        Assert.Equal(["node-a", "node-b"], nodes.Select(n => n.NodeId));
    }

    [Fact]
    public async Task NodesDisappearOnceTheirHeartbeatGoesStale()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var store = new InMemoryClusterStore { TimeProvider = time, HeartbeatTimeout = TimeSpan.FromSeconds(30) };

        await store.HeartbeatAsync(new NodeInfo { NodeId = "node-a", Address = "http://a" }, default);
        Assert.Single(await store.GetNodesAsync(default));

        time.Advance(TimeSpan.FromSeconds(31));

        // A node that stops heartbeating is treated as gone, which triggers reallocation.
        Assert.Empty(await store.GetNodesAsync(default));
    }

    private sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}

public class LeaderElectorTests
{
    private readonly InMemoryLeaderElector _elector = new();

    [Fact]
    public async Task FirstCallerBecomesLeader()
    {
        await using var lease = await _elector.TryAcquireAsync("allocator", default);

        Assert.NotNull(lease);
    }

    [Fact]
    public async Task SecondCallerIsRefusedWhileTheLeaseIsHeld()
    {
        await using var held = await _elector.TryAcquireAsync("allocator", default);

        Assert.Null(await _elector.TryAcquireAsync("allocator", default));
    }

    [Fact]
    public async Task ReleasingTheLeaseLetsAnotherInstanceTakeOver()
    {
        var first = await _elector.TryAcquireAsync("allocator", default);
        Assert.NotNull(first);
        await first.DisposeAsync();

        await using var second = await _elector.TryAcquireAsync("allocator", default);
        Assert.NotNull(second);
    }

    [Fact]
    public async Task ExpiryFreesALeaseHeldByACrashedLeader()
    {
        var abandoned = await _elector.TryAcquireAsync("allocator", default);
        Assert.NotNull(abandoned);

        // Nobody released it; the holder simply stopped renewing.
        _elector.Expire("allocator");

        await using var successor = await _elector.TryAcquireAsync("allocator", default);
        Assert.NotNull(successor);
    }

    [Fact]
    public async Task LosingTheLeaseSignalsTheHolder()
    {
        await using var lease = await _elector.TryAcquireAsync("allocator", default);
        Assert.NotNull(lease);
        Assert.False(lease.Lost.IsCancellationRequested);

        _elector.Expire("allocator");

        // The signal is what stops a deposed leader from continuing to act as one.
        Assert.True(lease.Lost.IsCancellationRequested);
    }

    [Fact]
    public async Task DifferentNamesAreIndependentLocks()
    {
        await using var one = await _elector.TryAcquireAsync("allocator", default);
        await using var two = await _elector.TryAcquireAsync("compactor", default);

        Assert.NotNull(one);
        Assert.NotNull(two);
    }

    [Fact]
    public async Task OnlyOneOfManyConcurrentContendersWins()
    {
        var attempts = await Task.WhenAll(
            Enumerable.Range(0, 32).Select(_ => _elector.TryAcquireAsync("allocator", default)));

        Assert.Single(attempts, lease => lease is not null);
    }
}
