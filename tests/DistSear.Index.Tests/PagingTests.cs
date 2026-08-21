using DistSear.Abstractions.Documents;
using DistSear.Abstractions.Search;
using DistSear.Analysis;
using DistSear.Index;
using DistSear.Index.Query;
using Xunit;

namespace DistSear.Index.Tests;

public class PagingTests
{
    private readonly ShardIndex _index;

    public PagingTests()
    {
        _index = new ShardIndex(TestCorpus.ProductMapping, new AnalyzerRegistry());

        // Deliberately includes duplicate prices so the tiebreaker is exercised.
        for (var i = 0; i < 40; i++)
        {
            _index.AddOrUpdate(new IndexedDocument
            {
                Id = $"doc-{i:D2}",
                ShardKey = "shard-0",
                Fields = new Dictionary<string, object?>
                {
                    ["title"] = "widget number " + i,
                    ["price"] = (double)(i / 4),
                    ["year"] = 2000L + i
                }
            });
        }

        _index.Refresh();
    }

    private Collectors.TopDocs Page(
        int from,
        int size,
        IReadOnlyList<SortSpec>? sort = null,
        IReadOnlyList<object?>? searchAfter = null) =>
        _index.Searcher.Search(new SearchExecution
        {
            Query = MatchAllQuery.Instance,
            Context = _index.CreateContext(),
            From = from,
            Size = size,
            Sort = sort ?? [new SortSpec("year")],
            SearchAfter = searchAfter
        }).TopDocs;

    [Fact]
    public void OffsetPagingWalksTheResultSetWithoutGapsOrRepeats()
    {
        var seen = new List<string>();

        for (var from = 0; from < 40; from += 10)
        {
            seen.AddRange(Page(from, 10).Hits.Select(h => h.ExternalId));
        }

        Assert.Equal(40, seen.Count);
        Assert.Equal(40, seen.Distinct().Count());
        Assert.Equal(seen.Order(), seen);
    }

    [Fact]
    public void TotalHitsIsTheFullMatchCountNotThePageSize()
    {
        var page = Page(0, 5);

        Assert.Equal(5, page.Hits.Count);
        Assert.Equal(40, page.TotalHits);
    }

    [Fact]
    public void SearchAfterContinuesFromTheLastHitOfThePreviousPage()
    {
        var first = Page(0, 10);
        var cursor = first.Hits[^1].SortValues;

        var second = Page(0, 10, searchAfter: cursor);

        Assert.Equal(
            Page(10, 10).Hits.Select(h => h.ExternalId),
            second.Hits.Select(h => h.ExternalId));
    }

    [Fact]
    public void SearchAfterWalksTheWholeResultSetExactlyOnce()
    {
        var seen = new List<string>();
        IReadOnlyList<object?>? cursor = null;

        while (true)
        {
            var page = Page(0, 7, searchAfter: cursor);

            if (page.Hits.Count == 0)
            {
                break;
            }

            seen.AddRange(page.Hits.Select(h => h.ExternalId));
            cursor = page.Hits[^1].SortValues;
        }

        Assert.Equal(40, seen.Count);
        Assert.Equal(40, seen.Distinct().Count());
    }

    [Fact]
    public void SearchAfterIsStableAcrossTiedSortValues()
    {
        // Prices repeat in groups of four, so paging by price relies entirely on the document-id
        // tiebreaker to avoid repeating or skipping rows.
        var seen = new List<string>();
        IReadOnlyList<object?>? cursor = null;

        while (true)
        {
            var page = Page(0, 3, sort: [new SortSpec("price")], searchAfter: cursor);

            if (page.Hits.Count == 0)
            {
                break;
            }

            seen.AddRange(page.Hits.Select(h => h.ExternalId));
            cursor = page.Hits[^1].SortValues;
        }

        Assert.Equal(40, seen.Count);
        Assert.Equal(40, seen.Distinct().Count());
    }

    [Fact]
    public void SortValuesAreReturnedSoTheyCanBeUsedAsACursor()
    {
        var page = Page(0, 3, sort: [new SortSpec("year"), new SortSpec("price")]);

        // Two declared keys plus the implicit document-id tiebreaker that makes the cursor total.
        Assert.All(page.Hits, h => Assert.Equal(3, h.SortValues.Length));
        Assert.Equal(2000.0, page.Hits[0].SortValues[0]);
        Assert.Equal("doc-00", page.Hits[0].SortValues[2]);
    }

    [Fact]
    public void PagingBeyondTheEndReturnsNothingRatherThanFailing()
    {
        var page = Page(100, 10);

        Assert.Empty(page.Hits);
        Assert.Equal(40, page.TotalHits);
    }

    [Fact]
    public void DescendingSearchAfterAlsoTerminates()
    {
        var seen = new List<string>();
        IReadOnlyList<object?>? cursor = null;

        while (true)
        {
            var page = Page(0, 9, sort: [new SortSpec("year", Descending: true)], searchAfter: cursor);

            if (page.Hits.Count == 0)
            {
                break;
            }

            seen.AddRange(page.Hits.Select(h => h.ExternalId));
            cursor = page.Hits[^1].SortValues;
        }

        Assert.Equal(40, seen.Distinct().Count());
        Assert.Equal("doc-39", seen[0]);
    }
}
