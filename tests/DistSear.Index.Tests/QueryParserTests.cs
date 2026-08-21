using DistSear.Analysis;
using DistSear.Index;
using DistSear.Index.Query;
using Xunit;

namespace DistSear.Index.Tests;

public class QueryParserTests
{
    private readonly QueryParser _parser = new(TestCorpus.ProductMapping, new AnalyzerRegistry());
    private readonly ShardIndex _index = TestCorpus.BuildProductIndex();

    private string Describe(string query) => _parser.Parse(query).Describe();

    private Dictionary<string, double> SearchWithScores(string query)
    {
        var outcome = _index.Searcher.Search(new SearchExecution
        {
            Query = _parser.Parse(query),
            Context = _index.CreateContext(),
            Size = 20
        });

        return outcome.TopDocs.Hits.ToDictionary(h => h.ExternalId, h => h.Score);
    }

    private IReadOnlyList<string> Search(string query)
    {
        var outcome = _index.Searcher.Search(new SearchExecution
        {
            Query = _parser.Parse(query),
            Context = _index.CreateContext(),
            Size = 20
        });

        return [.. outcome.TopDocs.Hits.Select(h => h.ExternalId)];
    }

    [Fact]
    public void BareTermTargetsTheDefaultField() =>
        Assert.Equal("title:search", Describe("search"));

    [Fact]
    public void FieldPrefixRetargetsTheClause() =>
        Assert.Equal("body:search", Describe("body:search"));

    [Fact]
    public void TermsAreAnalyzedWithTheSearchAnalyzer() =>
        // The english analyzer stems "searching" to "search" at both index and query time.
        Assert.Equal("title:search", Describe("searching"));

    [Fact]
    public void EmptyQueryMatchesEverything() =>
        Assert.Equal("*:*", Describe("   "));

    [Fact]
    public void DefaultOperatorCombinesBareTermsAsOptional() =>
        Assert.Equal("(title:fast title:search)", Describe("fast search"));

    [Fact]
    public void ExplicitAndMakesBothClausesRequired() =>
        Assert.Equal("(+title:fast +title:search)", Describe("fast AND search"));

    [Fact]
    public void PlusAndMinusMapToRequiredAndProhibited() =>
        Assert.Equal("(+title:fast -title:search)", Describe("+fast -search"));

    [Fact]
    public void NotIsEquivalentToMinus() =>
        Assert.Equal("(+title:fast -title:search)", Describe("+fast NOT search"));

    [Fact]
    public void GroupsControlPrecedence() =>
        Assert.Equal("(+(title:fast title:search) +title:engin)", Describe("(fast OR search) AND engine"));

    [Fact]
    public void PhrasesBecomePhraseQueries() =>
        Assert.Equal("title:\"distribut search\"", Describe("\"distributed search\""));

    [Fact]
    public void PhraseSlopIsParsed() =>
        Assert.Equal("body:\"search store\"~2", Describe("body:\"search stores\"~2"));

    [Fact]
    public void SinglewordPhraseCollapsesToATermQuery() =>
        Assert.Equal("title:search", Describe("\"search\""));

    [Fact]
    public void TrailingStarBecomesAPrefixQuery() =>
        Assert.Equal("category:kit*", Describe("category:kit*"));

    [Fact]
    public void InteriorWildcardBecomesAWildcardQuery() =>
        Assert.Equal("category:k*n", Describe("category:k*n"));

    [Fact]
    public void TildeBecomesAFuzzyQueryWithTheGivenBudget()
    {
        Assert.Equal("body:serch~1", Describe("body:serch~1"));
        Assert.Equal("body:serch~2", Describe("body:serch~"));
    }

    [Fact]
    public void CaretAppliesABoost() =>
        Assert.Equal("title:search^3", Describe("search^3"));

    [Fact]
    public void NumericRangesUseTheNumericQuery() =>
        Assert.Equal("price:[10 TO 50]", Describe("price:[10 TO 50]"));

    [Fact]
    public void ExclusiveRangeBracesAreHonoured() =>
        Assert.Equal("year:{2019 TO 2023}", Describe("year:{2019 TO 2023}"));

    [Fact]
    public void StarMeansUnboundedOnEitherSide()
    {
        Assert.Equal("price:[* TO 50]", Describe("price:[* TO 50]"));
        Assert.Equal("price:[10 TO *]", Describe("price:[10 TO *]"));
    }

    [Fact]
    public void KeywordRangesCompareLexicographically() =>
        Assert.Equal("category:[books TO kitchen]", Describe("category:[books TO kitchen]"));

    [Fact]
    public void HyphenInsideAWordIsNotAnOperator()
    {
        // Only a leading sign is an operator. The lexer keeps "e-mail" as one word, which the
        // analyzer then splits on punctuation into an optional pair -- crucially not into
        // "+body:e -body:mail", which is what treating the hyphen as an operator would produce.
        Assert.Equal("(body:e body:mail)", Describe("body:e-mail"));

        // Contrast with a genuine leading sign.
        Assert.Equal("(body:e -body:mail)", Describe("body:e -body:mail"));
    }

    [Fact]
    public void UnknownFieldIsRejectedWithItsPosition()
    {
        var error = Assert.Throws<QueryParseException>(() => _parser.Parse("nosuch:value"));

        Assert.Contains("nosuch", error.Message);
    }

    [Fact]
    public void UnterminatedPhraseIsRejected() =>
        Assert.Throws<QueryParseException>(() => _parser.Parse("\"never closed"));

    [Fact]
    public void UnclosedGroupIsRejected() =>
        Assert.Throws<QueryParseException>(() => _parser.Parse("(fast OR search"));

    [Fact]
    public void MalformedRangeIsRejected() =>
        Assert.Throws<QueryParseException>(() => _parser.Parse("price:[10 50]"));

    [Fact]
    public void ParsedQueriesActuallyRun()
    {
        Assert.Equal(["1"], Search("\"distributed search\""));
        Assert.Equal(["4", "5"], Search("category:kitchen").Order().ToArray());
        // Document 6 costs 19.99, so it belongs in this range even though it is not a book.
        Assert.Equal(["1", "2", "3", "6"], Search("price:[10 TO 50]").Order().ToArray());
        Assert.Contains("1", Search("body:serch~1"));
        Assert.DoesNotContain("6", Search("search -category:outdoors"));
    }

    [Fact]
    public void BoostChangesRankingNotMembership()
    {
        var unboosted = SearchWithScores("search OR distributed");
        var boosted = SearchWithScores("search OR distributed^10");

        // The matching set is unchanged: a boost reweights, it does not filter.
        Assert.Equal(unboosted.Keys.Order(), boosted.Keys.Order());

        // Document 2 matches only "distributed", document 3 only "search". Weighting
        // "distributed" ten times heavier must move document 2 up relative to document 3,
        // whatever their absolute order happened to be beforehand.
        var before = unboosted["2"] / unboosted["3"];
        var after = boosted["2"] / boosted["3"];

        Assert.True(after > before * 5, $"Boost barely moved the ratio: {before:F3} to {after:F3}.");
    }

    [Fact]
    public void BoostScalesTheClauseItAppliesTo()
    {
        var plain = SearchWithScores("distributed");
        var boosted = SearchWithScores("distributed^10");

        // Document 2 matches this single clause, so its score scales by exactly the boost.
        Assert.Equal(plain["2"] * 10, boosted["2"], 8);
    }
}
