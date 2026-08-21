using DistSear.Abstractions.Search;
using DistSear.Coordinator.Search;
using DistSear.Coordinator.Security;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Xunit;

namespace DistSear.IntegrationTests;

/// <summary>
/// The cache key is a security boundary, not just a performance detail: get it wrong and one
/// caller's results are served to another whose access differs.
/// </summary>
public class SearchCacheKeyTests
{
    private static readonly SearchRequest Request = new() { Index = "catalog", Query = "search", Size = 10 };

    [Fact]
    public void IdenticalRequestsFromTheSameCallerShareAKey()
    {
        var caller = new SearchPrincipal(["finance"]);

        Assert.Equal(
            CachingSearchExecutor.BuildKey(Request, caller),
            CachingSearchExecutor.BuildKey(Request, caller));
    }

    [Fact]
    public void CallersWithDifferentGroupsNeverShareAKey()
    {
        var finance = CachingSearchExecutor.BuildKey(Request, new SearchPrincipal(["finance"]));
        var legal = CachingSearchExecutor.BuildKey(Request, new SearchPrincipal(["legal"]));
        var none = CachingSearchExecutor.BuildKey(Request, new SearchPrincipal([]));

        Assert.NotEqual(finance, legal);
        Assert.NotEqual(finance, none);
        Assert.NotEqual(legal, none);
    }

    [Fact]
    public void GroupOrderDoesNotSplitTheCache()
    {
        // The same access, expressed in a different order, is the same access.
        Assert.Equal(
            CachingSearchExecutor.BuildKey(Request, new SearchPrincipal(["a", "b"])),
            CachingSearchExecutor.BuildKey(Request, new SearchPrincipal(["b", "a"])));
    }

    [Fact]
    public void AnUnrestrictedCallerIsKeyedApartFromAnAnonymousOne()
    {
        // Both have no groups, but one bypasses filtering entirely and must never reuse the other's
        // cached results.
        Assert.NotEqual(
            CachingSearchExecutor.BuildKey(Request, SearchPrincipal.Unrestricted),
            CachingSearchExecutor.BuildKey(Request, new SearchPrincipal([])));
    }

    [Theory]
    [MemberData(nameof(VaryingRequests))]
    public void EveryRequestFieldThatChangesTheAnswerChangesTheKey(SearchRequest varied)
    {
        var caller = new SearchPrincipal(["finance"]);

        Assert.NotEqual(
            CachingSearchExecutor.BuildKey(Request, caller),
            CachingSearchExecutor.BuildKey(varied, caller));
    }

    public static TheoryData<SearchRequest> VaryingRequests() =>
    [
        Request with { Index = "other" },
        Request with { Query = "different" },
        Request with { Filter = "category:books" },
        Request with { From = 10 },
        Request with { Size = 20 },
        Request with { SearchType = SearchType.DfsQueryThenFetch },
        Request with { Sort = [new SortSpec("price")] },
        Request with { Sort = [new SortSpec("price", Descending: true)] },
        Request with { Fields = ["title"] },
        Request with { SearchAfter = [1.0, "doc-1"] },
        Request with { Facets = [new FacetSpec { Name = "c", Field = "category" }] },
        Request with { Highlight = new HighlightSpec { Fields = ["title"] } }
    ];
}

public class CachingSearchExecutorTests
{
    private sealed class RecordingExecutor : ISearchExecutor
    {
        public int Calls { get; private set; }

        public bool ReturnPartial { get; set; }

        public Task<SearchResponse> SearchAsync(
            SearchRequest request,
            SearchPrincipal caller,
            CancellationToken cancellationToken = default)
        {
            Calls++;

            return Task.FromResult(new SearchResponse
            {
                Hits = [new SearchHit { Id = "1", Score = 1.0 }],
                TotalHits = 1,
                Shards = ReturnPartial
                    ? new ShardStatistics { Total = 2, Successful = 1, Failed = 1 }
                    : new ShardStatistics { Total = 2, Successful = 2 },
                TimedOut = ReturnPartial,
                TookMilliseconds = 42
            });
        }
    }

    private static CachingSearchExecutor Build(
        RecordingExecutor inner,
        out IDistributedCache cache,
        bool enabled = true)
    {
        cache = new MemoryDistributedCache(
            Options.Create(new MemoryDistributedCacheOptions()));

        return new CachingSearchExecutor(
            inner,
            cache,
            Options.Create(new CoordinatorOptions
            {
                CacheEnabled = enabled,
                CacheDuration = TimeSpan.FromMinutes(1)
            }));
    }

    private static readonly SearchRequest Request = new() { Index = "catalog", Query = "search" };

    [Fact]
    public async Task SecondIdenticalRequestIsServedFromCache()
    {
        var inner = new RecordingExecutor();
        var executor = Build(inner, out _);
        var caller = new SearchPrincipal(["finance"]);

        await executor.SearchAsync(Request, caller);
        var second = await executor.SearchAsync(Request, caller);

        Assert.Equal(1, inner.Calls);
        Assert.Equal(1, second.TotalHits);

        // A cache hit reports no elapsed time rather than repeating the original measurement.
        Assert.Equal(0, second.TookMilliseconds);
    }

    [Fact]
    public async Task ADifferentCallerDoesNotSeeTheCachedResult()
    {
        var inner = new RecordingExecutor();
        var executor = Build(inner, out _);

        await executor.SearchAsync(Request, new SearchPrincipal(["finance"]));
        await executor.SearchAsync(Request, new SearchPrincipal(["legal"]));

        // The whole point of keying on identity: this must reach the cluster, not the cache.
        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task PartialResultsAreNeverCached()
    {
        var inner = new RecordingExecutor { ReturnPartial = true };
        var executor = Build(inner, out _);
        var caller = new SearchPrincipal([]);

        await executor.SearchAsync(Request, caller);
        await executor.SearchAsync(Request, caller);

        // Caching a degraded answer would keep serving it after the cluster recovered.
        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task CachingCanBeTurnedOff()
    {
        var inner = new RecordingExecutor();
        var executor = Build(inner, out _, enabled: false);
        var caller = new SearchPrincipal([]);

        await executor.SearchAsync(Request, caller);
        await executor.SearchAsync(Request, caller);

        Assert.Equal(2, inner.Calls);
    }
}

public class ApiKeyTests
{
    [Fact]
    public void GeneratedKeysAreUniqueAndHighEntropy()
    {
        var keys = Enumerable.Range(0, 100).Select(_ => ApiKeyHasher.Generate()).ToList();

        Assert.Equal(100, keys.Distinct().Count());
        Assert.All(keys, k => Assert.Equal(64, k.Length));
    }

    [Fact]
    public void HashingIsStable() =>
        Assert.Equal(ApiKeyHasher.Hash("secret"), ApiKeyHasher.Hash("secret"));

    [Fact]
    public void DifferentKeysHashDifferently() =>
        Assert.NotEqual(ApiKeyHasher.Hash("secret-a"), ApiKeyHasher.Hash("secret-b"));

    [Fact]
    public void TheHashIsNotTheKey()
    {
        // Storing the key itself would make a leaked store a leaked set of credentials.
        const string key = "super-secret";

        Assert.DoesNotContain(key, ApiKeyHasher.Hash(key), StringComparison.Ordinal);
    }

    [Fact]
    public async Task LookupResolvesAKeyToItsGroups()
    {
        var store = new InMemoryApiKeyStore();
        var key = ApiKeyHasher.Generate();

        store.Add(key, new ApiKeyRecord("ingest", ["finance"], CanWrite: true));

        var found = await store.FindAsync(key, default);

        Assert.NotNull(found);
        Assert.Equal("ingest", found.Name);
        Assert.Equal(["finance"], found.Groups);
        Assert.True(found.CanWrite);
    }

    [Fact]
    public async Task AnUnknownKeyResolvesToNothing()
    {
        var store = new InMemoryApiKeyStore();
        store.Add(ApiKeyHasher.Generate(), new ApiKeyRecord("ingest", [], CanWrite: false));

        Assert.Null(await store.FindAsync(ApiKeyHasher.Generate(), default));
    }
}
