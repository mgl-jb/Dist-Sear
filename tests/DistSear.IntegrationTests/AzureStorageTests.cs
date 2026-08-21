using DistSear.Abstractions.Cluster;
using DistSear.Abstractions.Documents;
using DistSear.Abstractions.Mapping;
using DistSear.Abstractions.Storage;
using DistSear.Storage.Azure.Blobs;
using DistSear.Storage.Azure.Cosmos;
using Xunit;

namespace DistSear.IntegrationTests;

/// <summary>
/// Exercises the Azure implementations against the emulators.
///
/// The in-process stores already prove the cluster logic; what these add is proof that the Azure
/// SDKs are being driven correctly. Change-feed semantics, ETag concurrency and blob leases are
/// precisely where an in-memory stand-in can quietly diverge from the real service, so they are
/// what these tests target.
/// </summary>
[Collection(AzureEmulatorCollection.Name)]
[Trait("Category", "Integration")]
public class AzureStorageTests
{
    private readonly AzureEmulatorFixture _fixture;

    public AzureStorageTests(AzureEmulatorFixture fixture) => _fixture = fixture;

    private bool Skip() => _fixture.RequireEmulators();

    private CosmosDocumentStore Documents() => new(_fixture.Cosmos, _fixture.Options);

    private static IndexedDocument Doc(string id, string shardKey, bool deleted = false) => new()
    {
        Id = id,
        ShardKey = shardKey,
        Deleted = deleted,
        Fields = new Dictionary<string, object?>
        {
            ["title"] = "document " + id,
            ["count"] = 7L,
            ["price"] = 12.5,
            ["active"] = true,
            ["tags"] = new[] { "a", "b" }
        }
    };

    [SkippableFact]
    public async Task DocumentsWrittenToCosmosComeBackOnTheChangeFeed()
    {
        if (Skip()) return;

        var store = Documents();
        var shard = "shard-0";

        await store.UpsertAsync([Doc("1", shard), Doc("2", shard)], default);

        await using var cursor = store.OpenChangeFeed(shard, null);
        var batch = await cursor.ReadNextAsync(default);

        Assert.Equal(["1", "2"], batch.Documents.Select(d => d.Id).Order());
    }

    [SkippableFact]
    public async Task FieldTypesSurviveTheRoundTrip()
    {
        if (Skip()) return;

        var store = Documents();
        var shard = "shard-types";

        await store.UpsertAsync([Doc("typed", shard)], default);

        var found = await store.GetAsync(shard, ["typed"], default);
        var fields = Assert.Single(found).Fields;

        // Cosmos stores every number as an IEEE-754 double, so the integer 7 comes back off the
        // wire as 7.0. The store normalises whole numbers back to long, otherwise a year or a count
        // would reach the caller as 2021.0 depending purely on where it had been stored.
        Assert.IsType<long>(fields["count"]);
        Assert.Equal(7L, fields["count"]);

        // Genuine fractions are untouched.
        Assert.IsType<double>(fields["price"]);
        Assert.Equal(12.5, fields["price"]);
        Assert.Equal(true, fields["active"]);
        Assert.Equal("document typed", fields["title"]);
        Assert.Equal(["a", "b"], ((object?[])fields["tags"]!).Cast<string>());
    }

    [SkippableFact]
    public async Task TheFeedIsScopedToOneShardsPartition()
    {
        if (Skip()) return;

        var store = Documents();

        await store.UpsertAsync([Doc("a", "shard-1"), Doc("b", "shard-2")], default);

        await using var cursor = store.OpenChangeFeed("shard-1", null);
        var batch = await cursor.ReadNextAsync(default);

        // A node must never pay to read documents belonging to shards it does not own.
        Assert.Equal(["a"], batch.Documents.Select(d => d.Id));
    }

    [SkippableFact]
    public async Task DrainingTheFeedIsSignalledByNotModified()
    {
        if (Skip()) return;

        var store = Documents();
        var shard = "shard-drain";

        await store.UpsertAsync([Doc("1", shard)], default);

        await using var cursor = store.OpenChangeFeed(shard, null);

        var seen = new List<string>();
        ChangeFeedBatch batch;

        do
        {
            batch = await cursor.ReadNextAsync(default);
            seen.AddRange(batch.Documents.Select(d => d.Id));
        }
        while (batch.HasMore);

        // An empty page with status OK does not mean caught up; only NotModified does, and this
        // asserts the store reports that distinction rather than stopping early.
        Assert.Equal(["1"], seen);
        Assert.NotNull(batch.ContinuationToken);
    }

    [SkippableFact]
    public async Task AContinuationTokenResumesWithoutReplaying()
    {
        if (Skip()) return;

        var store = Documents();
        var shard = "shard-resume";

        await store.UpsertAsync([Doc("first", shard)], default);

        string? token;

        await using (var first = store.OpenChangeFeed(shard, null))
        {
            ChangeFeedBatch batch;

            do
            {
                batch = await first.ReadNextAsync(default);
            }
            while (batch.HasMore);

            token = batch.ContinuationToken;
        }

        await store.UpsertAsync([Doc("second", shard)], default);

        await using var resumed = store.OpenChangeFeed(shard, token);
        var seen = new List<string>();

        for (var i = 0; i < 5; i++)
        {
            var batch = await resumed.ReadNextAsync(default);
            seen.AddRange(batch.Documents.Select(d => d.Id));

            if (!batch.HasMore)
            {
                break;
            }
        }

        // Resuming must pick up only what arrived after the token, which is what makes snapshot
        // recovery cheap rather than a full replay.
        Assert.Equal(["second"], seen);
    }

    [SkippableFact]
    public async Task SoftDeletesTravelTheFeedAndVanishFromPointReads()
    {
        if (Skip()) return;

        var store = Documents();
        var shard = "shard-delete";

        await store.UpsertAsync([Doc("gone", shard)], default);
        await store.UpsertAsync([Doc("gone", shard, deleted: true)], default);

        await using var cursor = store.OpenChangeFeed(shard, null);
        var batch = await cursor.ReadNextAsync(default);

        // The whole reason deletes are tombstones: a hard delete would simply disappear from the
        // feed and replicas would never learn the document is gone.
        Assert.Contains(batch.Documents, d => d is { Id: "gone", Deleted: true });
        Assert.Empty(await store.GetAsync(shard, ["gone"], default));
    }

    [SkippableFact]
    public async Task BulkWritesSpanningManyShardsAllArrive()
    {
        if (Skip()) return;

        var store = Documents();

        var documents = Enumerable.Range(0, 120)
            .Select(i => Doc($"bulk-{i:D3}", $"shard-bulk-{i % 4}"))
            .ToList();

        await store.UpsertAsync(documents, default);

        var total = 0;

        for (var shard = 0; shard < 4; shard++)
        {
            await using var cursor = store.OpenChangeFeed($"shard-bulk-{shard}", null);
            ChangeFeedBatch batch;

            do
            {
                batch = await cursor.ReadNextAsync(default);
                total += batch.Documents.Count;
            }
            while (batch.HasMore);
        }

        // Exercises both the transactional-batch path and its 100-operation chunking.
        Assert.Equal(120, total);
    }
}

[Collection(AzureEmulatorCollection.Name)]
[Trait("Category", "Integration")]
public class CosmosClusterStoreTests
{
    private readonly AzureEmulatorFixture _fixture;

    public CosmosClusterStoreTests(AzureEmulatorFixture fixture) => _fixture = fixture;

    private bool Skip() => _fixture.RequireEmulators();

    private CosmosClusterStore Store() => new(_fixture.Cosmos, _fixture.Options);

    private static IndexMapping Mapping(string name) => new(
        name,
        [new FieldMapping { Name = "title", Type = FieldType.Text }],
        numberOfShards: 2,
        numberOfReplicas: 2);

    [SkippableFact]
    public async Task StateRoundTripsThroughCosmos()
    {
        if (Skip()) return;

        var store = Store();
        var initial = await store.GetAsync(default);

        var next = initial with
        {
            Indexes = new Dictionary<string, IndexMetadata>
            {
                ["catalog"] = new() { Mapping = Mapping("catalog"), CreatedAt = DateTimeOffset.UtcNow }
            },
            Shards =
            [
                new ShardAllocation
                {
                    Index = "catalog",
                    ShardId = 0,
                    Copies = [new ShardCopy { NodeId = "node-a", State = ShardState.Started, IsSnapshotOwner = true }]
                }
            ]
        };

        Assert.NotNull(await store.TryUpdateAsync(next, default));

        var reloaded = await store.GetAsync(default);

        Assert.Equal(2, reloaded.Indexes["catalog"].Mapping.NumberOfShards);
        Assert.Equal("title", reloaded.Indexes["catalog"].Mapping.Fields.Single().Name);

        var copy = reloaded.ShardsOf("catalog").Single().Copies.Single();
        Assert.Equal(ShardState.Started, copy.State);
        Assert.True(copy.IsSnapshotOwner);
    }

    [SkippableFact]
    public async Task AStaleWriteIsRejectedByTheETagCheck()
    {
        if (Skip()) return;

        var store = Store();
        var baseline = await store.GetAsync(default);

        var first = await store.TryUpdateAsync(
            baseline with { Aliases = new Dictionary<string, string> { ["live"] = "one" } },
            default);

        Assert.NotNull(first);

        // Second writer still holds the pre-update ETag, so its decision was made against a version
        // that no longer exists.
        var loser = await store.TryUpdateAsync(
            baseline with { Aliases = new Dictionary<string, string> { ["live"] = "two" } },
            default);

        Assert.Null(loser);

        var final = await store.GetAsync(default);
        Assert.Equal("one", final.ResolveIndex("live"));
    }

    [SkippableFact]
    public async Task HeartbeatsRegisterAndReturnNodes()
    {
        if (Skip()) return;

        var store = Store();

        await store.HeartbeatAsync(new NodeInfo { NodeId = "node-x", Address = "http://x" }, default);
        await store.HeartbeatAsync(new NodeInfo { NodeId = "node-y", Address = "http://y" }, default);

        var nodes = await store.GetNodesAsync(default);

        Assert.Contains(nodes, n => n.NodeId == "node-x" && n.Address == "http://x");
        Assert.Contains(nodes, n => n.NodeId == "node-y");
    }
}

[Collection(AzureEmulatorCollection.Name)]
[Trait("Category", "Integration")]
public class BlobStorageTests
{
    private readonly AzureEmulatorFixture _fixture;

    public BlobStorageTests(AzureEmulatorFixture fixture) => _fixture = fixture;

    private bool Skip() => _fixture.RequireEmulators();

    [SkippableFact]
    public async Task SegmentFilesAndManifestsRoundTrip()
    {
        if (Skip()) return;

        var store = new BlobSegmentStore(_fixture.Blobs, _fixture.Options);
        var payload = "segment bytes"u8.ToArray();

        using (var content = new MemoryStream(payload))
        {
            await store.WriteFileAsync("catalog", 0, 1, "segment-00000.dss", content, default);
        }

        await store.CommitAsync(
            new CommitManifest
            {
                Index = "catalog",
                ShardId = 0,
                Generation = 1,
                Files = ["segment-00000.dss"],
                ContinuationToken = "token-1",
                DocumentCount = 5,
                CreatedAt = DateTimeOffset.UtcNow
            },
            default);

        var manifest = await store.GetLatestManifestAsync("catalog", 0, default);

        Assert.NotNull(manifest);
        Assert.Equal("token-1", manifest.ContinuationToken);
        Assert.Equal(5, manifest.DocumentCount);

        await using var restored = await store.OpenReadAsync("catalog", 0, 1, "segment-00000.dss", default);
        using var buffer = new MemoryStream();
        await restored.CopyToAsync(buffer);

        Assert.Equal(payload, buffer.ToArray());
    }

    [SkippableFact]
    public async Task AShardWithNoSnapshotReportsNoManifest()
    {
        if (Skip()) return;

        var store = new BlobSegmentStore(_fixture.Blobs, _fixture.Options);

        // Not an error: it means the shard replays its partition in full.
        Assert.Null(await store.GetLatestManifestAsync("never-snapshotted", 7, default));
    }

    [SkippableFact]
    public async Task AnOlderSnapshotCannotOverwriteANewerOne()
    {
        if (Skip()) return;

        var store = new BlobSegmentStore(_fixture.Blobs, _fixture.Options);

        async Task CommitAsync(long generation, string token) =>
            await store.CommitAsync(
                new CommitManifest
                {
                    Index = "ordering",
                    ShardId = 0,
                    Generation = generation,
                    Files = [],
                    ContinuationToken = token,
                    CreatedAt = DateTimeOffset.UtcNow
                },
                default);

        await CommitAsync(5, "newer");
        await CommitAsync(2, "older");

        var manifest = await store.GetLatestManifestAsync("ordering", 0, default);

        // Uploads can finish out of order; a late-landing old snapshot must not roll a replica back.
        Assert.Equal(5, manifest!.Generation);
        Assert.Equal("newer", manifest.ContinuationToken);
    }

    [SkippableFact]
    public async Task CheckpointsRoundTrip()
    {
        if (Skip()) return;

        var store = new BlobCheckpointStore(_fixture.Blobs, _fixture.Options);

        Assert.Null(await store.GetAsync("node-a", "catalog", 0, default));

        await store.SetAsync("node-a", "catalog", 0, "token-42", default);

        Assert.Equal("token-42", await store.GetAsync("node-a", "catalog", 0, default));

        // Checkpoints are per node: one node's progress must not be read as another's.
        Assert.Null(await store.GetAsync("node-b", "catalog", 0, default));
    }

    [SkippableFact]
    public async Task OnlyOneInstanceHoldsTheLeaseAtATime()
    {
        if (Skip()) return;

        var elector = new BlobLeaseElector(_fixture.Blobs, _fixture.Options);

        await using var leader = await elector.TryAcquireAsync("election-exclusive", default);
        Assert.NotNull(leader);

        // This is the property the whole coordination design rests on.
        Assert.Null(await elector.TryAcquireAsync("election-exclusive", default));
    }

    [SkippableFact]
    public async Task ReleasingTheLeaseHandsOverImmediately()
    {
        if (Skip()) return;

        var elector = new BlobLeaseElector(_fixture.Blobs, _fixture.Options);

        var first = await elector.TryAcquireAsync("election-handover", default);
        Assert.NotNull(first);
        await first.DisposeAsync();

        // A graceful shutdown should not cost the cluster a full lease duration of leaderlessness.
        await using var second = await elector.TryAcquireAsync("election-handover", default);
        Assert.NotNull(second);
    }

    [SkippableFact]
    public async Task DifferentLeaseNamesAreIndependent()
    {
        if (Skip()) return;

        var elector = new BlobLeaseElector(_fixture.Blobs, _fixture.Options);

        await using var one = await elector.TryAcquireAsync("election-a", default);
        await using var two = await elector.TryAcquireAsync("election-b", default);

        Assert.NotNull(one);
        Assert.NotNull(two);
    }

    [SkippableFact]
    public async Task ReleasingSignalsTheHolderThatLeadershipIsGone()
    {
        if (Skip()) return;

        var elector = new BlobLeaseElector(_fixture.Blobs, _fixture.Options);
        var lease = await elector.TryAcquireAsync("election-signal", default);

        Assert.NotNull(lease);
        Assert.False(lease.Lost.IsCancellationRequested);

        await lease.DisposeAsync();

        // The signal is what stops a deposed leader from continuing to coordinate.
        Assert.True(lease.Lost.IsCancellationRequested);
    }
}
