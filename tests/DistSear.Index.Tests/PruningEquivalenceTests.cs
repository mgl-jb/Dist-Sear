using DistSear.Abstractions.Documents;
using DistSear.Abstractions.Mapping;
using DistSear.Abstractions.Search;
using DistSear.Analysis;
using DistSear.Index;
using DistSear.Index.Query;
using Xunit;

namespace DistSear.Index.Tests;

/// <summary>
/// WAND is an optimisation: it must change how much work is done and nothing else. These tests run
/// the same query with pruning on and off over randomised corpora and assert the results are
/// identical, which is the only way to have real confidence in a pruning scorer.
/// </summary>
public class PruningEquivalenceTests
{
    private static readonly IndexMapping Mapping = new(
        "random",
        [new FieldMapping { Name = "body", Type = FieldType.Text, Analyzer = AnalyzerRegistry.Simple }],
        defaultField: "body");

    private static ShardIndex BuildRandomIndex(Random random, int documentCount, int vocabularySize)
    {
        var index = new ShardIndex(Mapping, new AnalyzerRegistry());

        for (var i = 0; i < documentCount; i++)
        {
            var length = random.Next(1, 25);
            var words = new List<string>(length);

            for (var w = 0; w < length; w++)
            {
                // Zipf-ish skew: low-numbered terms appear far more often, which is what makes the
                // IDF spread wide enough for pruning to actually engage.
                var pick = (int)(vocabularySize * Math.Pow(random.NextDouble(), 2));
                words.Add("t" + Math.Min(pick, vocabularySize - 1));
            }

            index.AddOrUpdate(new IndexedDocument
            {
                Id = i.ToString(),
                ShardKey = "shard-0",
                Fields = new Dictionary<string, object?> { ["body"] = string.Join(' ', words) }
            });
        }

        index.Refresh();
        return index;
    }

    /// <summary>
    /// Runs a query either fully pruned or fully exhaustive.
    ///
    /// Pruning only begins once the collector has counted past its total-hits threshold, so the
    /// threshold is forced to zero here. Without that these corpora are far too small to ever cross
    /// the default and the comparison would be vacuous.
    /// </summary>
    private static Collectors.TopDocs Execute(
        ShardIndex index,
        Query.Query query,
        int size,
        bool pruning,
        IReadOnlyList<SortSpec>? sort = null)
    {
        var context = new SearchContext
        {
            Mapping = index.Mapping,
            Analyzers = index.Analyzers,
            Statistics = index.LocalStatistics(),
            EnableTopKPruning = pruning
        };

        return index.Searcher.Search(new SearchExecution
        {
            Query = query,
            Context = context,
            Size = size,
            Sort = sort ?? [],
            TotalHitsThreshold = pruning ? 0 : long.MaxValue
        }).TopDocs;
    }

    private static IReadOnlyList<(string Id, double Score)> Run(
        ShardIndex index,
        Query.Query query,
        int size,
        bool pruning,
        IReadOnlyList<SortSpec>? sort = null) =>
        [.. Execute(index, query, size, pruning, sort).Hits.Select(h => (h.ExternalId, h.Score))];

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(42)]
    [InlineData(1337)]
    [InlineData(2024)]
    public void PrunedTopKMatchesExhaustiveScoringExactly(int seed)
    {
        var random = new Random(seed);
        var index = BuildRandomIndex(random, documentCount: 400, vocabularySize: 60);
        var prunedSomething = false;

        for (var trial = 0; trial < 25; trial++)
        {
            var termCount = random.Next(2, 6);
            var clauses = new List<BooleanClause>();

            for (var t = 0; t < termCount; t++)
            {
                var term = "t" + random.Next(0, 60);
                clauses.Add(new BooleanClause(new TermQuery("body", term), Occur.Should));
            }

            var query = new BooleanQuery(clauses);
            var size = random.Next(1, 20);

            var pruned = Execute(index, query, size, pruning: true);
            var exhaustive = Execute(index, query, size, pruning: false);

            Assert.Equal(exhaustive.Hits.Count, pruned.Hits.Count);

            for (var i = 0; i < exhaustive.Hits.Count; i++)
            {
                Assert.Equal(exhaustive.Hits[i].ExternalId, pruned.Hits[i].ExternalId);
                Assert.Equal(exhaustive.Hits[i].Score, pruned.Hits[i].Score, 10);
            }

            // Pruning must never invent hits, and must have actually skipped some work for this
            // comparison to mean anything.
            Assert.True(pruned.TotalHits <= exhaustive.TotalHits);
            prunedSomething |= pruned.TotalHits < exhaustive.TotalHits;
        }

        Assert.True(prunedSomething, "Pruning never engaged, so the comparison proved nothing.");
    }

    [Fact]
    public void PruningIsAlsoExactWhenEveryTermIsCommon()
    {
        // Uniform term frequencies give near-identical IDFs, the case where the score bounds are
        // least discriminating and an off-by-one in the pivot would show up.
        var random = new Random(99);
        var index = new ShardIndex(Mapping, new AnalyzerRegistry());

        for (var i = 0; i < 200; i++)
        {
            var words = Enumerable.Range(0, 10).Select(_ => "t" + random.Next(0, 5));

            index.AddOrUpdate(new IndexedDocument
            {
                Id = i.ToString(),
                ShardKey = "shard-0",
                Fields = new Dictionary<string, object?> { ["body"] = string.Join(' ', words) }
            });
        }

        index.Refresh();

        var query = new BooleanQuery(
        [
            new BooleanClause(new TermQuery("body", "t0"), Occur.Should),
            new BooleanClause(new TermQuery("body", "t1"), Occur.Should),
            new BooleanClause(new TermQuery("body", "t2"), Occur.Should)
        ]);

        Assert.Equal(Run(index, query, 10, pruning: false), Run(index, query, 10, pruning: true));
    }

    [Fact]
    public void PruningDoesNotDropTiedScores()
    {
        // Identical documents produce identical scores; a pruning bound compared with the wrong
        // strictness would silently drop some of them.
        var index = new ShardIndex(Mapping, new AnalyzerRegistry());

        for (var i = 0; i < 50; i++)
        {
            index.AddOrUpdate(new IndexedDocument
            {
                Id = i.ToString(),
                ShardKey = "shard-0",
                Fields = new Dictionary<string, object?> { ["body"] = "alpha beta" }
            });
        }

        index.Refresh();

        var query = new BooleanQuery(
        [
            new BooleanClause(new TermQuery("body", "alpha"), Occur.Should),
            new BooleanClause(new TermQuery("body", "beta"), Occur.Should)
        ]);

        var pruned = Run(index, query, 10, pruning: true);

        Assert.Equal(10, pruned.Count);
        Assert.Equal(Run(index, query, 10, pruning: false), pruned);
    }

    [Fact]
    public void TotalsStayExactUntilTheThresholdIsCrossed()
    {
        // The corpus is smaller than the default threshold, so pruning never starts and the total
        // is exact even though the scorer is a WAND.
        var index = BuildRandomIndex(new Random(5), documentCount: 300, vocabularySize: 40);

        var query = new BooleanQuery(
        [
            new BooleanClause(new TermQuery("body", "t1"), Occur.Should),
            new BooleanClause(new TermQuery("body", "t2"), Occur.Should)
        ]);

        var context = new SearchContext
        {
            Mapping = index.Mapping,
            Analyzers = index.Analyzers,
            Statistics = index.LocalStatistics()
        };

        var defaults = index.Searcher.Search(new SearchExecution
        {
            Query = query,
            Context = context,
            Size = 5
        }).TopDocs;

        var exhaustive = Execute(index, query, 5, pruning: false);

        Assert.False(defaults.TotalIsLowerBound);
        Assert.Equal(exhaustive.TotalHits, defaults.TotalHits);
    }

    [Fact]
    public void OnceThePruningThresholdIsCrossedTheTotalIsReportedAsALowerBound()
    {
        var index = BuildRandomIndex(new Random(11), documentCount: 400, vocabularySize: 40);

        var query = new BooleanQuery(
        [
            new BooleanClause(new TermQuery("body", "t1"), Occur.Should),
            new BooleanClause(new TermQuery("body", "t2"), Occur.Should),
            new BooleanClause(new TermQuery("body", "t9"), Occur.Should)
        ]);

        var pruned = Execute(index, query, 5, pruning: true);
        var exhaustive = Execute(index, query, 5, pruning: false);

        // The ranking is still exact; only the count is affected, and it says so.
        Assert.Equal(
            exhaustive.Hits.Select(h => h.ExternalId),
            pruned.Hits.Select(h => h.ExternalId));

        Assert.True(pruned.TotalIsLowerBound);
        Assert.True(pruned.TotalHits <= exhaustive.TotalHits);
        Assert.False(exhaustive.TotalIsLowerBound);
    }
}
