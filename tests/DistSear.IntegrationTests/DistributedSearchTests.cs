using DistSear.Abstractions.Cluster;
using DistSear.Abstractions.Search;
using DistSear.Coordinator.Search;
using Xunit;

namespace DistSear.IntegrationTests;

/// <summary>
/// End-to-end tests over a real multi-node cluster running in process. Every layer below the
/// transport is production code: routing, change-feed indexing, recovery, fan-out and merging.
/// </summary>
public class DistributedSearchTests
{
    private static readonly IncomingDocument[] Catalog =
    [
        TestCluster.Document("1", "Fast distributed search", "searching large corpora quickly", "books", 29.99, 2021),
        TestCluster.Document("2", "Distributed systems design", "systems that scale", "books", 49.50, 2019),
        TestCluster.Document("3", "Search engine internals", "how an engine ranks documents", "books", 39.00, 2023),
        TestCluster.Document("4", "Coffee grinder", "burr grinder", "kitchen", 89.00, 2022),
        TestCluster.Document("5", "Espresso machine", "steams milk", "kitchen", 499.00, 2023),
        TestCluster.Document("6", "Search lighting", "torch for searching", "outdoors", 19.99, 2020)
    ];

    private static async Task<TestCluster> BuildAsync(
        int nodes = 3,
        int shards = 4,
        int replicas = 2,
        CoordinatorOptions? options = null)
    {
        var cluster = await TestCluster.StartAsync(nodes, options);

        await cluster.CreateIndexAsync(TestCluster.CatalogMapping(shards, replicas));
        await cluster.IndexAsync("catalog", Catalog);
        await cluster.StabiliseAsync();

        return cluster;
    }

    private static IReadOnlyList<string> Ids(SearchResponse response) =>
        [.. response.Hits.Select(h => h.Id)];

    [Fact]
    public async Task DocumentsAreSpreadAcrossShardsAndAllAreFound()
    {
        await using var cluster = await BuildAsync();

        var response = await cluster.SearchAsync(new SearchRequest { Index = "catalog", Size = 20 });

        Assert.Equal(6, response.TotalHits);
        Assert.Equal(["1", "2", "3", "4", "5", "6"], Ids(response).Order().ToArray());

        // If everything landed on one shard the fan-out would not be exercised at all.
        var shardsHit = response.Hits.Select(h => h.ShardId).Distinct().Count();
        Assert.True(shardsHit > 1, $"All documents routed to {shardsHit} shard(s).");
    }

    [Fact]
    public async Task SearchAcrossShardsFindsTheSameDocumentsAsASingleShardIndex()
    {
        await using var distributed = await BuildAsync(nodes: 3, shards: 4, replicas: 2);
        await using var single = await BuildAsync(nodes: 1, shards: 1, replicas: 1);

        var fromMany = await distributed.SearchAsync("catalog", "search", size: 20);
        var fromOne = await single.SearchAsync("catalog", "search", size: 20);

        Assert.Equal(Ids(fromOne).Order(), Ids(fromMany).Order());
        Assert.Equal(fromOne.TotalHits, fromMany.TotalHits);
    }

    [Fact]
    public async Task RankingIsConsistentAcrossShardsWhenGlobalStatisticsAreUsed()
    {
        await using var distributed = await BuildAsync(nodes: 3, shards: 4, replicas: 2);
        await using var single = await BuildAsync(nodes: 1, shards: 1, replicas: 1);

        var request = new SearchRequest
        {
            Index = "catalog",
            Query = "search OR distributed",
            Size = 20,
            SearchType = SearchType.DfsQueryThenFetch
        };

        var fromMany = await distributed.SearchAsync(request);
        var fromOne = await single.SearchAsync(request with { SearchType = SearchType.QueryThenFetch });

        // With globally-summed document frequencies the sharded ranking matches the unsharded one,
        // which is exactly what the DFS pre-pass exists to guarantee.
        Assert.Equal(Ids(fromOne), Ids(fromMany));
    }

    [Fact]
    public async Task FetchPhaseReturnsStoredFieldsForTheWinnersOnly()
    {
        await using var cluster = await BuildAsync();

        cluster.Transport.FetchCalls.Clear();

        var response = await cluster.SearchAsync(new SearchRequest
        {
            Index = "catalog",
            Query = "espresso",
            Size = 5
        });

        Assert.Single(response.Hits);
        Assert.Equal("Espresso machine", response.Hits[0].Fields["title"]);

        // Only the shard owning the single winner should have been asked for a document body.
        var fetched = cluster.Transport.FetchCalls.Values.Sum();
        Assert.Equal(1, fetched);
    }

    [Fact]
    public async Task RequestedFieldsAreProjected()
    {
        await using var cluster = await BuildAsync();

        var response = await cluster.SearchAsync(new SearchRequest
        {
            Index = "catalog",
            Query = "espresso",
            Fields = ["title"]
        });

        Assert.Equal(["title"], response.Hits[0].Fields.Keys);
    }

    [Fact]
    public async Task HighlightingWorksThroughTheFetchPhase()
    {
        await using var cluster = await BuildAsync();

        var response = await cluster.SearchAsync(new SearchRequest
        {
            Index = "catalog",
            Query = "title:distributed",
            Highlight = new HighlightSpec { Fields = ["title"] }
        });

        Assert.All(response.Hits, hit =>
            Assert.Contains("<em>", hit.Highlights["title"][0]));
    }

    [Fact]
    public async Task FacetsAreMergedAcrossShards()
    {
        await using var cluster = await BuildAsync();

        var response = await cluster.SearchAsync(new SearchRequest
        {
            Index = "catalog",
            Size = 20,
            Facets = [new FacetSpec { Name = "by_category", Field = "category", Size = 10 }]
        });

        var counts = response.Facets["by_category"].Buckets.ToDictionary(b => b.Key, b => b.Count);

        Assert.Equal(3, counts["books"]);
        Assert.Equal(2, counts["kitchen"]);
        Assert.Equal(1, counts["outdoors"]);
    }

    [Fact]
    public async Task RangeFacetsAreExactAcrossShards()
    {
        await using var cluster = await BuildAsync();

        var response = await cluster.SearchAsync(new SearchRequest
        {
            Index = "catalog",
            Size = 20,
            Facets =
            [
                new FacetSpec
                {
                    Name = "by_price",
                    Field = "price",
                    Kind = FacetKind.Range,
                    Ranges = [new FacetRange("under_50", null, 50), new FacetRange("from_50", 50, null)]
                }
            ]
        });

        var facet = response.Facets["by_price"];
        var counts = facet.Buckets.ToDictionary(b => b.Key, b => b.Count);

        Assert.Equal(4, counts["under_50"]);
        Assert.Equal(2, counts["from_50"]);

        // Caller-defined ranges are counted identically on every shard, so nothing is estimated.
        Assert.Equal(0, facet.DocCountErrorUpperBound);
    }

    [Fact]
    public async Task SortingAndPagingWorkAcrossShards()
    {
        await using var cluster = await BuildAsync();

        var first = await cluster.SearchAsync(new SearchRequest
        {
            Index = "catalog",
            Size = 3,
            Sort = [new SortSpec("price")]
        });

        var second = await cluster.SearchAsync(new SearchRequest
        {
            Index = "catalog",
            From = 3,
            Size = 3,
            Sort = [new SortSpec("price")]
        });

        Assert.Equal(["6", "1", "3"], Ids(first));
        Assert.Equal(["2", "4", "5"], Ids(second));
    }

    [Fact]
    public async Task SearchAfterPagesAcrossShardsWithoutGapsOrRepeats()
    {
        await using var cluster = await BuildAsync();

        var seen = new List<string>();
        IReadOnlyList<object?>? cursor = null;

        while (true)
        {
            var page = await cluster.SearchAsync(new SearchRequest
            {
                Index = "catalog",
                Size = 2,
                Sort = [new SortSpec("year")],
                SearchAfter = cursor
            });

            if (page.Hits.Count == 0)
            {
                break;
            }

            seen.AddRange(page.Hits.Select(h => h.Id));
            cursor = page.Hits[^1].SortValues;
        }

        Assert.Equal(6, seen.Count);
        Assert.Equal(6, seen.Distinct().Count());
    }

    [Fact]
    public async Task UpdatesReplaceDocumentsEverywhere()
    {
        await using var cluster = await BuildAsync();

        await cluster.IndexAsync("catalog",
            [TestCluster.Document("1", "Completely rewritten", category: "books")]);

        await cluster.StabiliseAsync();

        var response = await cluster.SearchAsync(new SearchRequest { Index = "catalog", Size = 20 });

        Assert.Equal(6, response.TotalHits);
        Assert.Empty((await cluster.SearchAsync("catalog", "title:fast")).Hits);
        Assert.Single((await cluster.SearchAsync("catalog", "title:rewritten")).Hits);
    }

    [Fact]
    public async Task DeletesPropagateThroughTheChangeFeed()
    {
        await using var cluster = await BuildAsync();

        await cluster.DeleteAsync("catalog", ["4", "5"]);
        await cluster.StabiliseAsync();

        var response = await cluster.SearchAsync(new SearchRequest { Index = "catalog", Size = 20 });

        Assert.Equal(4, response.TotalHits);
        Assert.DoesNotContain("4", Ids(response));
        Assert.DoesNotContain("5", Ids(response));
    }

    [Fact]
    public async Task EveryReplicaOfAShardHoldsTheSameDocuments()
    {
        await using var cluster = await BuildAsync(nodes: 3, shards: 2, replicas: 3);

        var byShard = new Dictionary<int, List<int>>();

        foreach (var nodeId in cluster.NodeIds)
        {
            foreach (var shard in cluster.Host(nodeId).Shards)
            {
                if (!byShard.TryGetValue(shard.ShardId, out var counts))
                {
                    counts = [];
                    byShard[shard.ShardId] = counts;
                }

                counts.Add(shard.Index.DocumentCount);
            }
        }

        // Replicas derive independently from the same change feed, so they must converge on
        // identical content without any replication protocol between them.
        Assert.All(byShard, entry => Assert.Single(entry.Value.Distinct()));
    }

    [Fact]
    public async Task AliasSwapRedirectsQueriesAtomically()
    {
        await using var cluster = await TestCluster.StartAsync(2);

        await cluster.CreateIndexAsync(TestCluster.CatalogMapping(2, 1));
        await cluster.IndexAsync("catalog", Catalog);
        await cluster.StabiliseAsync();

        await cluster.Controller.SetAliasAsync("live", "catalog", default);

        var viaAlias = await cluster.SearchAsync("live", "search", size: 20);
        var direct = await cluster.SearchAsync("catalog", "search", size: 20);

        Assert.Equal(Ids(direct).Order(), Ids(viaAlias).Order());
    }

    [Fact]
    public async Task UnknownIndexIsReportedClearly()
    {
        await using var cluster = await BuildAsync();

        await Assert.ThrowsAsync<IndexNotFoundException>(
            () => cluster.SearchAsync("nope", "anything"));
    }

    [Fact]
    public async Task DeepOffsetPagingIsRefusedWithAnActionableMessage()
    {
        await using var cluster = await BuildAsync(options: new CoordinatorOptions { MaxPageSize = 100 });

        var error = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => cluster.SearchAsync(new SearchRequest { Index = "catalog", From = 500, Size = 10 }));

        Assert.Contains("search_after", error.Message);
    }
}
