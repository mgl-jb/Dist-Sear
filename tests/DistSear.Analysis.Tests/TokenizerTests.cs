using DistSear.Analysis;
using Xunit;

namespace DistSear.Analysis.Tests;

public class StandardTokenizerTests
{
    private static List<Token> Tokenize(string text) =>
        StandardTokenizer.Instance.Tokenize(text).ToList();

    [Fact]
    public void SplitsOnPunctuationAndWhitespace()
    {
        var terms = Tokenize("Fast, distributed search!").Select(t => t.Term);

        Assert.Equal(["Fast", "distributed", "search"], terms);
    }

    [Fact]
    public void AssignsConsecutivePositions()
    {
        var positions = Tokenize("one two three").Select(t => t.Position);

        Assert.Equal([0, 1, 2], positions);
    }

    [Fact]
    public void RecordsOffsetsThatSliceBackToTheOriginalText()
    {
        const string text = "the quick brown fox";

        foreach (var token in Tokenize(text))
        {
            Assert.Equal(token.Term, text[token.StartOffset..token.EndOffset]);
        }
    }

    [Fact]
    public void KeepsDigitsAndMixedAlphanumerics()
    {
        var terms = Tokenize("model 3 x1000 v2").Select(t => t.Term);

        Assert.Equal(["model", "3", "x1000", "v2"], terms);
    }

    [Fact]
    public void HandlesNonBmpCharactersWithoutSplittingSurrogatePairs()
    {
        // U+1D400 MATHEMATICAL BOLD CAPITAL A is a letter outside the BMP.
        var tokens = Tokenize("\U0001D400\U0001D401 plain");

        Assert.Equal("\U0001D400\U0001D401", tokens[0].Term);
        Assert.Equal("plain", tokens[1].Term);
    }

    [Fact]
    public void ReturnsNothingForEmptyOrPunctuationOnlyInput()
    {
        Assert.Empty(Tokenize(""));
        Assert.Empty(Tokenize("   ... --- "));
    }

    [Fact]
    public void KeywordTokenizerEmitsTheWholeValueAsOneTerm()
    {
        var tokens = KeywordTokenizer.Instance.Tokenize("Electronics & Gadgets").ToList();

        Assert.Single(tokens);
        Assert.Equal("Electronics & Gadgets", tokens[0].Term);
    }
}
