using DistSear.Abstractions.Cluster;
using DistSear.Abstractions.Search;
using DistSear.Coordinator.Search;
using Xunit;

namespace DistSear.IntegrationTests;

/// <summary>
/// The failure behaviour the design actually promises: partial results instead of errors, failover
/// between replicas, and recovery of a lost node from snapshot plus change feed.
/// </summary>
public class ResilienceTests
{
    private static IReadOnlyList<IncomingDocument> Corpus(int count = 40) =>
    [
        .. Enumerable.Range(0, count).Select(i => TestCluster.Document(
            $"doc-{i:D3}",
            $"widget {i} search",
            category: i % 2 == 0 ? "even" : "odd",
            price: i,
            year: 2000 + i))
    ];

    private static async Task<TestCluster> BuildAsync(
        int nodes,
        int shards,
        int replicas,
        CoordinatorOptions? options = null,
        int documents = 40)
    {
        var cluster = await TestCluster.StartAsync(nodes, options);

        await cluster.CreateIndexAsync(TestCluster.CatalogMapping(shards, replicas));
        await cluster.IndexAsync("catalog", Corpus(documents));
        await cluster.StabiliseAsync();

        return cluster;
    }

    [Fact]
    public async Task AFailingReplicaIsTransparentWhenAnotherCopyExists()
    {
        await using var cluster = await BuildAsync(nodes: 3, shards: 4, replicas: 2);

        var healthy = await cluster.SearchAsync(new SearchRequest { Index = "catalog", Size = 50 });
        Assert.Equal(40, healthy.TotalHits);

        cluster.Transport.Fail("node-a");

        var degraded = await cluster.SearchAsync(new SearchRequest { Index = "catalog", Size = 50 });

        // Every shard has a second copy, so the failure should not be visible in the results.
        Assert.Equal(40, degraded.TotalHits);
        Assert.Equal(0, degraded.Shards.Failed);
    }

    [Fact]
    public async Task LosingEveryCopyOfAShardYieldsPartialResultsNotAnError()
    {
        // One copy per shard, so killing a node genuinely removes shards from reach.
        await using var cluster = await BuildAsync(nodes: 3, shards: 3, replicas: 1);

        foreach (var nodeId in cluster.NodeIds)
        {
            cluster.Transport.Fail(nodeId);
        }

        cluster.Transport.Heal("node-a");

        var response = await cluster.SearchAsync(new SearchRequest { Index = "catalog", Size = 50 });

        // The query still answers, and says exactly how much of the cluster it could not reach.
        Assert.True(response.Shards.Failed > 0);
        Assert.True(response.Shards.Successful > 0);
        Assert.True(response.TimedOut);
        Assert.True(response.TotalIsLowerBound);
        Assert.NotEmpty(response.Hits);
        Assert.All(response.Shards.Failures, f => Assert.NotEmpty(f.Reason));
    }

    [Fact]
    public async Task ASlowReplicaIsBoundedByThePerShardDeadline()
    {
        await using var cluster = await BuildAsync(
            nodes: 3,
            shards: 3,
            replicas: 1,
            options: new CoordinatorOptions
            {
                ShardTimeout = TimeSpan.FromMilliseconds(150),
                MaxReplicaAttempts = 1
            });

        cluster.Transport.Hang("node-a");

        var started = DateTimeOffset.UtcNow;
        var response = await cluster.SearchAsync(new SearchRequest { Index = "catalog", Size = 50 });
        var elapsed = DateTimeOffset.UtcNow - started;

        // A hung shard must not hold the whole query open indefinitely.
        Assert.True(elapsed < TimeSpan.FromSeconds(5), $"Query took {elapsed.TotalSeconds:F1}s.");
        Assert.True(response.Shards.Failed > 0);
        Assert.Contains(response.Shards.Failures, f => f.Reason.Contains("exceeded", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheCoordinatorRetriesOnADifferentReplica()
    {
        await using var cluster = await BuildAsync(
            nodes: 3,
            shards: 2,
            replicas: 3,
            options: new CoordinatorOptions { MaxReplicaAttempts = 2 });

        cluster.Transport.Fail("node-a");
        cluster.Transport.QueryCalls.Clear();

        var response = await cluster.SearchAsync(new SearchRequest { Index = "catalog", Size = 50 });

        Assert.Equal(40, response.TotalHits);
        Assert.Equal(0, response.Shards.Failed);
    }

    [Fact]
    public async Task AdaptiveSelectionSteersAwayFromAFailingNode()
    {
        await using var cluster = await BuildAsync(nodes: 3, shards: 2, replicas: 3);

        cluster.Transport.Fail("node-a");

        for (var i = 0; i < 5; i++)
        {
            await cluster.SearchAsync(new SearchRequest { Index = "catalog", Size = 10 });
        }

        cluster.Transport.QueryCalls.Clear();

        for (var i = 0; i < 5; i++)
        {
            await cluster.SearchAsync(new SearchRequest { Index = "catalog", Size = 10 });
        }

        var toFailing = cluster.Transport.QueryCalls.GetValueOrDefault("node-a");
        var toHealthy = cluster.Transport.QueryCalls
            .Where(kv => kv.Key != "node-a")
            .Sum(kv => kv.Value);

        // Repeated failures should push traffic towards the replicas that work.
        Assert.True(toHealthy > toFailing, $"Failing node still took {toFailing} of {toFailing + toHealthy} calls.");
    }

    [Fact]
    public async Task ShardsAreReassignedWhenANodeDies()
    {
        await using var cluster = await BuildAsync(nodes: 3, shards: 4, replicas: 2);

        cluster.KillNode("node-c");
        await cluster.StabiliseAsync();

        var allocations = await cluster.AllocationsAsync("catalog");

        Assert.DoesNotContain(allocations.SelectMany(a => a.Copies), c => c.NodeId == "node-c");
        Assert.All(allocations, a => Assert.NotEmpty(a.Searchable));
    }

    [Fact]
    public async Task ARebuiltReplicaRecoversEveryDocument()
    {
        await using var cluster = await BuildAsync(nodes: 3, shards: 4, replicas: 2);

        cluster.KillNode("node-c");
        await cluster.StabiliseAsync();

        // A fresh node takes over the orphaned shards and rebuilds them from the change feed.
        await cluster.AddNodeAsync("node-d");
        await cluster.StabiliseAsync();

        var response = await cluster.SearchAsync(new SearchRequest { Index = "catalog", Size = 50 });

        Assert.Equal(40, response.TotalHits);
        Assert.Equal(0, response.Shards.Failed);
    }

    [Fact]
    public async Task RecoveryRestoresFromASnapshotRatherThanReplayingEverything()
    {
        await using var cluster = await BuildAsync(nodes: 2, shards: 2, replicas: 1);

        await cluster.SnapshotAllAsync();

        // A brand new node inherits the shards and must restore them from durable storage.
        cluster.KillNode("node-a");
        await cluster.StabiliseAsync();
        await cluster.AddNodeAsync("node-z");
        await cluster.StabiliseAsync();

        var response = await cluster.SearchAsync(new SearchRequest { Index = "catalog", Size = 50 });
        Assert.Equal(40, response.TotalHits);

        var restored = cluster.Host("node-z").Shards;
        Assert.NotEmpty(restored);

        // The snapshot carried the bulk of the corpus, so replay only covered what came after it.
        Assert.All(restored, shard => Assert.True(
            shard.Indexer.DocumentsApplied < 40,
            $"Shard {shard.ShardId} replayed {shard.Indexer.DocumentsApplied} documents despite a snapshot."));
    }

    [Fact]
    public async Task WritesArrivingDuringRecoveryAreNotLost()
    {
        await using var cluster = await BuildAsync(nodes: 3, shards: 3, replicas: 1);

        await cluster.SnapshotAllAsync();
        cluster.KillNode("node-c");

        // These land in Cosmos while a shard has no owner at all.
        await cluster.IndexAsync("catalog",
        [
            TestCluster.Document("late-1", "late arrival one search"),
            TestCluster.Document("late-2", "late arrival two search")
        ]);

        await cluster.AddNodeAsync("node-d");
        await cluster.StabiliseAsync();

        var response = await cluster.SearchAsync(new SearchRequest { Index = "catalog", Size = 60 });

        // The change feed is the source of truth, so a write made while a shard was down is picked
        // up as soon as some node takes ownership.
        Assert.Equal(42, response.TotalHits);
    }

    [Fact]
    public async Task ShardsOnlyServeOnceTheyHaveCaughtUp()
    {
        await using var cluster = await TestCluster.StartAsync(2);

        await cluster.CreateIndexAsync(TestCluster.CatalogMapping(2, 1));
        await cluster.IndexAsync("catalog", Corpus(20));
        await cluster.StabiliseAsync();

        var allocations = await cluster.AllocationsAsync("catalog");

        Assert.All(
            allocations.SelectMany(a => a.Copies),
            copy => Assert.Equal(ShardState.Started, copy.State));
    }

    [Fact]
    public async Task ReplayingTheChangeFeedTwiceDoesNotDuplicateDocuments()
    {
        await using var cluster = await BuildAsync(nodes: 2, shards: 2, replicas: 1, documents: 20);

        // Rewind every shard and let it re-apply everything it has already seen.
        foreach (var nodeId in cluster.NodeIds)
        {
            foreach (var shard in cluster.Host(nodeId).Shards)
            {
                shard.Indexer.SeekTo(null);
            }
        }

        await cluster.StabiliseAsync();

        var response = await cluster.SearchAsync(new SearchRequest { Index = "catalog", Size = 60 });

        // At-least-once delivery is only safe because indexing is an upsert keyed by document id.
        Assert.Equal(20, response.TotalHits);
    }

    [Fact]
    public async Task DocumentLevelSecurityIsEnforcedAcrossEveryShard()
    {
        await using var cluster = await TestCluster.StartAsync(3);

        await cluster.CreateIndexAsync(TestCluster.CatalogMapping(4, 2));

        await cluster.IndexAsync("catalog",
        [
            .. Enumerable.Range(0, 10).Select(i => TestCluster.Document(
                $"open-{i}",
                $"public report {i}")),
            .. Enumerable.Range(0, 10).Select(i => TestCluster.Document(
                $"secret-{i}",
                $"public report {i}",
                acl: ["finance"]))
        ]);

        await cluster.StabiliseAsync();

        var anonymous = await cluster.SearchAsync(
            new SearchRequest { Index = "catalog", Size = 50 },
            new SearchPrincipal([]));

        var privileged = await cluster.SearchAsync(
            new SearchRequest { Index = "catalog", Size = 50 },
            new SearchPrincipal(["finance"]));

        Assert.Equal(10, anonymous.TotalHits);
        Assert.All(anonymous.Hits, h => Assert.StartsWith("open-", h.Id));

        Assert.Equal(20, privileged.TotalHits);
    }

    [Fact]
    public async Task RestrictedDocumentsAreHiddenFromCountsAsWellAsHits()
    {
        await using var cluster = await TestCluster.StartAsync(2);

        await cluster.CreateIndexAsync(TestCluster.CatalogMapping(2, 1));

        await cluster.IndexAsync("catalog",
        [
            TestCluster.Document("open", "shared report"),
            TestCluster.Document("closed", "shared report", acl: ["legal"])
        ]);

        await cluster.StabiliseAsync();

        var response = await cluster.SearchAsync(
            new SearchRequest { Index = "catalog", Query = "shared", Size = 10 },
            new SearchPrincipal([]));

        // Leaking the existence of a document through its count is still a leak.
        Assert.Equal(1, response.TotalHits);
    }
}
