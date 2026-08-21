using DistSear.Analysis;
using DistSear.Analysis.Filters;
using Xunit;

namespace DistSear.Analysis.Tests;

public class FilterTests
{
    private static List<Token> Run(ITokenFilter filter, string text) =>
        filter.Filter(StandardTokenizer.Instance.Tokenize(text)).ToList();

    [Fact]
    public void LowercaseFoldsCase()
    {
        var terms = Run(LowercaseFilter.Instance, "The QUICK Fox").Select(t => t.Term);

        Assert.Equal(["the", "quick", "fox"], terms);
    }

    [Theory]
    [InlineData("résumé", "resume")]
    [InlineData("naïve", "naive")]
    [InlineData("Zürich", "Zurich")]
    [InlineData("plain", "plain")]
    public void AsciiFoldingStripsDiacritics(string input, string expected) =>
        Assert.Equal(expected, AsciiFoldingFilter.Fold(input));

    [Fact]
    public void StopwordRemovalLeavesAPositionGap()
    {
        // "the" is dropped, but "quick" keeps position 1 so a phrase query for "quick fox"
        // cannot match this text at adjacent positions.
        var tokens = Run(
            StopwordFilter.English,
            "the quick the fox");

        Assert.Equal(["quick", "fox"], tokens.Select(t => t.Term));
        Assert.Equal([1, 3], tokens.Select(t => t.Position));
    }

    [Theory]
    [InlineData("searching", "search")]
    [InlineData("searches", "search")]
    [InlineData("searched", "search")]
    [InlineData("distributed", "distribut")]
    public void PorterStemConflatesInflections(string input, string expected)
    {
        var terms = Run(new PorterStemFilter(), input).Select(t => t.Term);

        Assert.Equal([expected], terms);
    }

    [Fact]
    public void SynonymsAreEmittedAtTheSamePositionAsTheOriginal()
    {
        var filter = SynonymFilter.FromRules(["tv, television, telly"]);
        var tokens = Run(filter, "cheap tv");

        Assert.Equal(["cheap", "tv", "television", "telly"], tokens.Select(t => t.Term));

        // Every expansion of "tv" shares its position, so phrase alignment is preserved.
        Assert.All(tokens.Where(t => t.Term != "cheap"), t => Assert.Equal(1, t.Position));
    }

    [Fact]
    public void SynonymRulesAreBidirectionalWithinAGroup()
    {
        var filter = SynonymFilter.FromRules(["tv, television"]);

        Assert.Equal(["television", "tv"], Run(filter, "television").Select(t => t.Term));
    }

    [Fact]
    public void EdgeNGramsProduceEveryPrefixInRange()
    {
        var terms = Run(new EdgeNGramFilter(2, 4), "search").Select(t => t.Term);

        Assert.Equal(["se", "sea", "sear"], terms);
    }

    [Fact]
    public void EdgeNGramsStopAtTheTermLength()
    {
        var terms = Run(new EdgeNGramFilter(1, 10), "go").Select(t => t.Term);

        Assert.Equal(["g", "go"], terms);
    }
}
