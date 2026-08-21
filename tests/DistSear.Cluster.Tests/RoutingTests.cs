using DistSear.Cluster.Routing;
using Xunit;

namespace DistSear.Cluster.Tests;

public class Murmur3Tests
{
    [Fact]
    public void MatchesKnownReferenceVectors()
    {
        // Standard MurmurHash3 x86_32 test vectors. Routing decisions are baked into Cosmos
        // partition keys, so this hash must never drift between versions or runtimes.
        Assert.Equal(0x00000000u, Murmur3.Hash32(""));
        Assert.Equal(0x514E28B7u, Murmur3.Hash32("", 1));
        Assert.Equal(0x81F16F39u, Murmur3.Hash32("", 0xffffffff));
        Assert.Equal(0x7FA09EA6u, Murmur3.Hash32("a", 0x9747b28c));
        Assert.Equal(0xF0478627u, Murmur3.Hash32("abcd", 0x9747b28c));
        Assert.Equal(0x5A97808Au, Murmur3.Hash32("aaaa", 0x9747b28c));
        Assert.Equal(0x24884CBAu, Murmur3.Hash32("Hello, world!", 0x9747b28c));
        Assert.Equal(0xB3DD93FAu, Murmur3.Hash32("abc"));
    }

    [Fact]
    public void IsDeterministic() =>
        Assert.Equal(Murmur3.Hash32("document-42"), Murmur3.Hash32("document-42"));

    [Fact]
    public void SimilarInputsProduceDissimilarHashes()
    {
        // Sequential ids are the common case; if they hashed to neighbouring values, shard
        // assignment would clump badly.
        var a = Murmur3.Hash32("doc-1");
        var b = Murmur3.Hash32("doc-2");

        Assert.True(Math.Abs((long)a - b) > 1000);
    }
}

public class DocumentRouterTests
{
    [Fact]
    public void RoutesConsistently()
    {
        var first = DocumentRouter.ShardFor("order-1234", 8);

        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(first, DocumentRouter.ShardFor("order-1234", 8));
        }
    }

    [Fact]
    public void AlwaysProducesAShardInRange()
    {
        for (var i = 0; i < 5000; i++)
        {
            var shard = DocumentRouter.ShardFor($"doc-{i}", 16);

            Assert.InRange(shard, 0, 15);
        }
    }

    [Fact]
    public void DistributesDocumentsEvenlyAcrossShards()
    {
        const int shards = 8;
        const int documents = 40_000;

        var counts = new int[shards];

        for (var i = 0; i < documents; i++)
        {
            counts[DocumentRouter.ShardFor($"doc-{i}", shards)]++;
        }

        var expected = documents / (double)shards;

        // Every shard should land within a few percent of its fair share.
        Assert.All(counts, count =>
            Assert.InRange(count, expected * 0.94, expected * 1.06));
    }

    [Fact]
    public void ExplicitRoutingKeyCoLocatesRelatedDocuments()
    {
        var shards = new[] { "a", "b", "c", "d" }
            .Select(id => DocumentRouter.ShardFor(id, "tenant-7", 16))
            .Distinct()
            .ToList();

        // Every document routed by the same tenant key lands on one shard, so that tenant's
        // queries can be answered without a fan-out.
        Assert.Single(shards);
    }

    [Fact]
    public void ShardKeysRoundTrip()
    {
        for (var shard = 0; shard < 32; shard++)
        {
            Assert.True(DocumentRouter.TryParseShardKey(DocumentRouter.ShardKey(shard), out var parsed));
            Assert.Equal(shard, parsed);
        }
    }

    [Theory]
    [InlineData("shard-")]
    [InlineData("shard-x")]
    [InlineData("nope-1")]
    [InlineData("")]
    public void MalformedShardKeysAreRejected(string key) =>
        Assert.False(DocumentRouter.TryParseShardKey(key, out _));

    [Fact]
    public void ChangingShardCountChangesRouting()
    {
        // The reason shard count is fixed at index creation: doubling it re-routes most documents,
        // so growing an index means reindexing behind an alias.
        var moved = Enumerable.Range(0, 1000)
            .Count(i => DocumentRouter.ShardFor($"doc-{i}", 4) != DocumentRouter.ShardFor($"doc-{i}", 8));

        Assert.True(moved > 400, $"Only {moved} of 1000 documents moved, which looks wrong.");
    }
}

public class RendezvousHashTests
{
    private static readonly string[] Nodes = ["node-a", "node-b", "node-c", "node-d"];

    [Fact]
    public void PlacementIsDeterministic()
    {
        var first = RendezvousHash.Select("shard-3", Nodes, 2);
        var second = RendezvousHash.Select("shard-3", Nodes, 2);

        Assert.Equal(first, second);
    }

    [Fact]
    public void SelectsTheRequestedNumberOfReplicas()
    {
        Assert.Single(RendezvousHash.Select("shard-0", Nodes, 1));
        Assert.Equal(3, RendezvousHash.Select("shard-0", Nodes, 3).Count);
    }

    [Fact]
    public void NeverSelectsTheSameNodeTwice()
    {
        var selected = RendezvousHash.Select("shard-1", Nodes, 3);

        Assert.Equal(selected.Count, selected.Distinct().Count());
    }

    [Fact]
    public void CapsAtTheNumberOfAvailableNodes() =>
        Assert.Equal(4, RendezvousHash.Select("shard-0", Nodes, 10).Count);

    [Fact]
    public void ReturnsNothingWhenThereAreNoNodes() =>
        Assert.Empty(RendezvousHash.Select("shard-0", [], 2));

    [Fact]
    public void SpreadsShardsAcrossNodes()
    {
        var owners = Enumerable.Range(0, 64)
            .Select(shard => RendezvousHash.Select(DocumentRouter.ShardKey(shard), Nodes, 1)[0])
            .GroupBy(n => n)
            .ToDictionary(g => g.Key, g => g.Count());

        Assert.Equal(4, owners.Count);

        // Each node should own roughly a quarter of 64 shards.
        Assert.All(owners.Values, count => Assert.InRange(count, 8, 24));
    }

    [Fact]
    public void RemovingANodeOnlyMovesThatNodesShards()
    {
        const int shards = 200;
        var reduced = Nodes.Where(n => n != "node-c").ToArray();

        var moved = 0;
        var movedFromRemovedNode = 0;

        for (var shard = 0; shard < shards; shard++)
        {
            var key = DocumentRouter.ShardKey(shard);
            var before = RendezvousHash.Select(key, Nodes, 1)[0];
            var after = RendezvousHash.Select(key, reduced, 1)[0];

            if (before == after)
            {
                continue;
            }

            moved++;

            if (before == "node-c")
            {
                movedFromRemovedNode++;
            }
        }

        // This is the property that makes rendezvous hashing worth using: nothing reshuffles except
        // the shards that actually lost their host.
        Assert.Equal(moved, movedFromRemovedNode);
        Assert.True(moved > 0, "Removing a node should have moved something.");
    }

    [Fact]
    public void AddingANodeOnlyPullsShardsOntoIt()
    {
        var expanded = Nodes.Append("node-e").ToArray();
        var movedElsewhere = 0;

        for (var shard = 0; shard < 200; shard++)
        {
            var key = DocumentRouter.ShardKey(shard);
            var before = RendezvousHash.Select(key, Nodes, 1)[0];
            var after = RendezvousHash.Select(key, expanded, 1)[0];

            if (before != after && after != "node-e")
            {
                movedElsewhere++;
            }
        }

        Assert.Equal(0, movedElsewhere);
    }

    [Fact]
    public void NodeAndShardNamesCannotCollideThroughConcatenation()
    {
        // Length-delimiting the shard key prevents ("ab","c") hashing the same as ("a","bc").
        Assert.NotEqual(RendezvousHash.Weight("ab", "c"), RendezvousHash.Weight("a", "bc"));
    }
}
