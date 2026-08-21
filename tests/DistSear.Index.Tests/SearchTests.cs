using DistSear.Abstractions.Search;
using DistSear.Analysis;
using DistSear.Index;
using DistSear.Index.Facets;
using DistSear.Index.Query;
using Xunit;

namespace DistSear.Index.Tests;

public class SearchTests
{
    private readonly ShardIndex _index = TestCorpus.BuildProductIndex();

    private IReadOnlyList<string> Search(
        Query.Query query,
        int size = 10,
        IReadOnlyList<SortSpec>? sort = null,
        IReadOnlyCollection<string>? principals = null)
    {
        var outcome = _index.Searcher.Search(new SearchExecution
        {
            Query = query,
            Context = _index.CreateContext(),
            Size = size,
            Sort = sort ?? [],
            Principals = principals
        });

        return [.. outcome.TopDocs.Hits.Select(h => h.ExternalId)];
    }

    [Fact]
    public void TermQueryFindsStemmedMatches()
    {
        // "searching" in the body stems to the same term as the query "search".
        var ids = Search(new TermQuery("body", "search"));

        Assert.Contains("1", ids);
        Assert.Contains("3", ids);
    }

    [Fact]
    public void MustClausesIntersect()
    {
        var query = new BooleanQuery(
        [
            new BooleanClause(new TermQuery("title", "search"), Occur.Must),
            new BooleanClause(new TermQuery("title", "distribut"), Occur.Must)
        ]);

        Assert.Equal(["1"], Search(query));
    }

    [Fact]
    public void ShouldClausesUnionAndRankByHowManyMatched()
    {
        var query = new BooleanQuery(
        [
            new BooleanClause(new TermQuery("title", "search"), Occur.Should),
            new BooleanClause(new TermQuery("title", "distribut"), Occur.Should)
        ]);

        var ids = Search(query);

        // Document 1 matches both clauses, so it must rank ahead of those matching only one.
        Assert.Equal("1", ids[0]);
        Assert.Contains("2", ids);
        Assert.Contains("3", ids);
    }

    [Fact]
    public void MustNotExcludes()
    {
        var query = new BooleanQuery(
        [
            new BooleanClause(new TermQuery("title", "search"), Occur.Must),
            new BooleanClause(new TermQuery("category", "outdoors"), Occur.MustNot)
        ]);

        Assert.DoesNotContain("6", Search(query));
    }

    [Fact]
    public void FilterClauseRestrictsWithoutAffectingRanking()
    {
        var scored = new BooleanQuery([new BooleanClause(new TermQuery("title", "search"), Occur.Must)]);

        var filtered = new BooleanQuery(
        [
            new BooleanClause(new TermQuery("title", "search"), Occur.Must),
            new BooleanClause(new TermQuery("category", "books"), Occur.Filter)
        ]);

        var scoredHits = _index.Searcher.Search(new SearchExecution
        {
            Query = scored,
            Context = _index.CreateContext(),
            Size = 10
        }).TopDocs.Hits.ToDictionary(h => h.ExternalId, h => h.Score);

        var filteredHits = _index.Searcher.Search(new SearchExecution
        {
            Query = filtered,
            Context = _index.CreateContext(),
            Size = 10
        }).TopDocs.Hits;

        // The filter removes the non-book match but leaves every surviving score untouched.
        Assert.Contains("6", scoredHits.Keys);
        Assert.DoesNotContain(filteredHits, h => h.ExternalId == "6");
        Assert.NotEmpty(filteredHits);
        Assert.All(filteredHits, h => Assert.Equal(scoredHits[h.ExternalId], h.Score, 10));
    }

    [Fact]
    public void ShouldClausesBecomeOptionalOnceAFilterIsPresent()
    {
        // Matching the standard rule: minimum_should_match defaults to 1 only when the query has no
        // required clause. A filter counts as required, so the should clause turns into a pure
        // ranking signal and non-matching documents come back with score zero.
        var query = new BooleanQuery(
        [
            new BooleanClause(new TermQuery("title", "search"), Occur.Should),
            new BooleanClause(new TermQuery("category", "books"), Occur.Filter)
        ]);

        var hits = _index.Searcher.Search(new SearchExecution
        {
            Query = query,
            Context = _index.CreateContext(),
            Size = 10
        }).TopDocs.Hits;

        Assert.Equal(["1", "2", "3"], hits.Select(h => h.ExternalId).Order().ToArray());
        Assert.Equal(0, hits.Single(h => h.ExternalId == "2").Score);
        Assert.True(hits.Single(h => h.ExternalId == "1").Score > 0);
    }

    [Fact]
    public void MinimumShouldMatchRequiresSeveralClauses()
    {
        var query = new BooleanQuery(
            [
                new BooleanClause(new TermQuery("title", "search"), Occur.Should),
                new BooleanClause(new TermQuery("title", "distribut"), Occur.Should),
                new BooleanClause(new TermQuery("title", "engin"), Occur.Should)
            ],
            minimumShouldMatch: 2);

        var ids = Search(query);

        Assert.Contains("1", ids);
        Assert.Contains("3", ids);
        Assert.DoesNotContain("2", ids);
    }

    [Fact]
    public void PhraseQueryRequiresAdjacency()
    {
        Assert.Equal(["1"], Search(new PhraseQuery("title", ["distribut", "search"])));

        // The same terms in the other order are not adjacent in that order anywhere.
        Assert.Empty(Search(new PhraseQuery("title", ["search", "distribut"])));
    }

    [Fact]
    public void SlopAllowsInterveningWords()
    {
        // Document 3 body: "How a search engine stores and ranks documents".
        Assert.Empty(Search(new PhraseQuery("body", ["search", "store"])));
        Assert.Equal(["3"], Search(new PhraseQuery("body", ["search", "store"], slop: 2)));
    }

    [Fact]
    public void PhraseDoesNotMatchAcrossSeparateValuesOfAMultiValuedField()
    {
        var index = new ShardIndex(TestCorpus.ProductMapping, new AnalyzerRegistry());

        index.AddOrUpdate(new Abstractions.Documents.IndexedDocument
        {
            Id = "x",
            ShardKey = "shard-0",
            Fields = new Dictionary<string, object?>
            {
                ["title"] = new[] { "red fox", "brown dog" }
            }
        });

        index.Refresh();

        var hits = index.Searcher.Search(new SearchExecution
        {
            Query = new PhraseQuery("title", ["fox", "brown"]),
            Context = index.CreateContext(),
            Size = 10
        });

        // Without the position gap between values, "fox brown" would look adjacent.
        Assert.Empty(hits.TopDocs.Hits);
    }

    [Fact]
    public void PrefixQueryMatchesEveryTermWithThatPrefix()
    {
        var ids = Search(new PrefixQuery("category", "kit"));

        Assert.Equal(["4", "5"], ids.Order().ToArray());
    }

    [Theory]
    [InlineData("kitchen", true)]
    [InlineData("kit*", true)]
    [InlineData("*chen", true)]
    [InlineData("k?tchen", true)]
    [InlineData("k*n", true)]
    [InlineData("kitchens", false)]
    [InlineData("k?chen", false)]
    public void WildcardPatternsMatchAsExpected(string pattern, bool shouldMatch) =>
        Assert.Equal(shouldMatch, WildcardQuery.Matches(pattern, "kitchen"));

    [Fact]
    public void WildcardQueryFindsMatchingTerms()
    {
        var ids = Search(new WildcardQuery("category", "*door*"));

        Assert.Equal(["6"], ids);
    }

    [Fact]
    public void FuzzyQueryToleratesTypos()
    {
        // "serch" is one deletion away from the indexed term "search".
        var ids = Search(new FuzzyQuery("body", "serch", maxEdits: 1));

        Assert.Contains("1", ids);
        Assert.Contains("3", ids);
    }

    [Fact]
    public void FuzzyQueryRespectsTheEditBudget()
    {
        Assert.Empty(Search(new FuzzyQuery("category", "kitchennnn", maxEdits: 1)));
        Assert.NotEmpty(Search(new FuzzyQuery("category", "kitchan", maxEdits: 1)));
    }

    [Fact]
    public void NumericRangeFiltersOnDocValues()
    {
        var query = new NumericRangeQuery("price", 20.0, 50.0);
        var ids = Search(query).Order().ToArray();

        Assert.Equal(["1", "2", "3"], ids);
    }

    [Fact]
    public void ExclusiveRangeBoundsAreHonoured()
    {
        var inclusive = new NumericRangeQuery("year", 2021, 2023);
        var exclusive = new NumericRangeQuery("year", 2021, 2023, includeLower: false, includeUpper: false);

        Assert.Equal(4, Search(inclusive).Count);
        Assert.Equal(["4"], Search(exclusive));
    }

    [Fact]
    public void SortsByFieldAscendingAndDescending()
    {
        var ascending = Search(MatchAllQuery.Instance, sort: [new SortSpec("price")]);
        var descending = Search(MatchAllQuery.Instance, sort: [new SortSpec("price", Descending: true)]);

        Assert.Equal(["6", "1", "3", "2", "4", "5"], ascending);
        Assert.Equal(ascending.Reverse(), descending);
    }

    [Fact]
    public void DeletedDocumentsDisappearAfterRefresh()
    {
        Assert.Contains("4", Search(MatchAllQuery.Instance));

        _index.Delete("4");
        _index.Refresh();

        Assert.DoesNotContain("4", Search(MatchAllQuery.Instance));
        Assert.Equal(5, _index.DocumentCount);
    }

    [Fact]
    public void UpdatingADocumentReplacesRatherThanDuplicatesIt()
    {
        var index = TestCorpus.BuildProductIndex();

        index.AddOrUpdate(TestCorpus.Product("1", "Completely different title", "new body", "books", 1.0, 2024));
        index.Refresh();

        var hits = index.Searcher.Search(new SearchExecution
        {
            Query = MatchAllQuery.Instance,
            Context = index.CreateContext(),
            Size = 50
        }).TopDocs;

        Assert.Equal(6, hits.TotalHits);
        Assert.Single(hits.Hits, h => h.ExternalId == "1");

        // The old terms are gone.
        var stale = index.Searcher.Search(new SearchExecution
        {
            Query = new TermQuery("title", "fast"),
            Context = index.CreateContext(),
            Size = 10
        }).TopDocs;

        Assert.Empty(stale.Hits);
    }

    [Fact]
    public void WritesAreInvisibleUntilRefresh()
    {
        var index = TestCorpus.BuildProductIndex();
        index.AddOrUpdate(TestCorpus.Product("99", "Brand new", "body", "books", 5.0, 2024));

        var before = index.Searcher.Search(new SearchExecution
        {
            Query = MatchAllQuery.Instance,
            Context = index.CreateContext(),
            Size = 50
        }).TopDocs.TotalHits;

        index.Refresh();

        var after = index.Searcher.Search(new SearchExecution
        {
            Query = MatchAllQuery.Instance,
            Context = index.CreateContext(),
            Size = 50
        }).TopDocs.TotalHits;

        Assert.Equal(6, before);
        Assert.Equal(7, after);
    }

    [Fact]
    public void DocumentLevelSecurityHidesDocumentsTheCallerCannotSee()
    {
        var index = new ShardIndex(TestCorpus.ProductMapping, new AnalyzerRegistry());

        index.AddOrUpdate(TestCorpus.Product("public", "Open report", "b", "books", 1, 2020));
        index.AddOrUpdate(TestCorpus.Product("secret", "Open report", "b", "books", 1, 2020, acl: ["group-a"]));
        index.Refresh();

        IReadOnlyList<string> SearchAs(IReadOnlyCollection<string>? principals) =>
        [
            .. index.Searcher.Search(new SearchExecution
            {
                Query = MatchAllQuery.Instance,
                Context = index.CreateContext(),
                Size = 10,
                Principals = principals
            }).TopDocs.Hits.Select(h => h.ExternalId)
        ];

        Assert.Equal(["public"], SearchAs([]));
        Assert.Equal(["public"], SearchAs(["group-b"]));
        Assert.Equal(2, SearchAs(["group-a"]).Count);

        // Null principals means enforcement is off, for internal calls only.
        Assert.Equal(2, SearchAs(null).Count);
    }

    [Fact]
    public void AclFilteringAlsoCorrectsTheTotalHitCount()
    {
        var index = new ShardIndex(TestCorpus.ProductMapping, new AnalyzerRegistry());
        index.AddOrUpdate(TestCorpus.Product("a", "report", "b", "books", 1, 2020, acl: ["group-a"]));
        index.AddOrUpdate(TestCorpus.Product("b", "report", "b", "books", 1, 2020));
        index.Refresh();

        var outcome = index.Searcher.Search(new SearchExecution
        {
            Query = MatchAllQuery.Instance,
            Context = index.CreateContext(),
            Size = 10,
            Principals = []
        });

        // A hidden document must not leak through the count either.
        Assert.Equal(1, outcome.TopDocs.TotalHits);
    }

    [Fact]
    public void TermsFacetCountsPerCategory()
    {
        var facet = new TermsFacetCollector(new FacetSpec { Name = "by_category", Field = "category" });

        _index.Searcher.Search(new SearchExecution
        {
            Query = MatchAllQuery.Instance,
            Context = _index.CreateContext(),
            Size = 10,
            Facets = [facet]
        });

        var result = facet.Build();
        var counts = result.Buckets.ToDictionary(b => b.Key, b => b.Count);

        Assert.Equal(3, counts["books"]);
        Assert.Equal(2, counts["kitchen"]);
        Assert.Equal(1, counts["outdoors"]);
        Assert.Equal(0, result.DocCountErrorUpperBound);
    }

    [Fact]
    public void MultiValuedFacetCountsEachValue()
    {
        var facet = new TermsFacetCollector(new FacetSpec { Name = "by_tag", Field = "tags" });

        _index.Searcher.Search(new SearchExecution
        {
            Query = MatchAllQuery.Instance,
            Context = _index.CreateContext(),
            Size = 10,
            Facets = [facet]
        });

        var counts = facet.Build().Buckets.ToDictionary(b => b.Key, b => b.Count);

        Assert.Equal(2, counts["search"]);
        Assert.Equal(2, counts["systems"]);
        Assert.Equal(2, counts["coffee"]);
    }

    [Fact]
    public void RangeFacetBucketsAreHalfOpen()
    {
        var facet = new RangeFacetCollector(new FacetSpec
        {
            Name = "by_price",
            Field = "price",
            Kind = FacetKind.Range,
            Ranges =
            [
                new FacetRange("cheap", null, 30),
                new FacetRange("mid", 30, 100),
                new FacetRange("dear", 100, null)
            ]
        });

        _index.Searcher.Search(new SearchExecution
        {
            Query = MatchAllQuery.Instance,
            Context = _index.CreateContext(),
            Size = 10,
            Facets = [facet]
        });

        var counts = facet.Build().Buckets.ToDictionary(b => b.Key, b => b.Count);

        Assert.Equal(2, counts["cheap"]);
        Assert.Equal(3, counts["mid"]);
        Assert.Equal(1, counts["dear"]);
    }

    [Fact]
    public void FacetsCountOnlyDocumentsMatchingTheQuery()
    {
        var facet = new TermsFacetCollector(new FacetSpec { Name = "by_category", Field = "category" });

        _index.Searcher.Search(new SearchExecution
        {
            Query = new NumericRangeQuery("price", null, 40.0),
            Context = _index.CreateContext(),
            Size = 10,
            Facets = [facet]
        });

        var counts = facet.Build().Buckets.ToDictionary(b => b.Key, b => b.Count);

        Assert.Equal(2, counts["books"]);
        Assert.Equal(1, counts["outdoors"]);
        Assert.False(counts.ContainsKey("kitchen"));
    }
}
