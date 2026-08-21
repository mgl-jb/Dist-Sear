using DistSear.Analysis;
using DistSear.Index;
using DistSear.Index.Query;
using DistSear.Index.Scoring;
using Xunit;

namespace DistSear.Index.Tests;

public class Bm25ScoringTests
{
    private const double K1 = 1.2;
    private const double B = 0.75;

    /// <summary>
    /// Two documents over one field, chosen so every BM25 input can be worked out by hand:
    ///   doc1 "alpha beta"        length 2
    ///   doc2 "alpha alpha gamma" length 3
    /// so N = 2, avgdl = 2.5, df(alpha) = 2 and df(beta) = 1.
    /// </summary>
    private static ShardIndex BuildTwoDocumentIndex()
    {
        var index = new ShardIndex(TestCorpus.SimpleMapping, new AnalyzerRegistry());
        index.AddOrUpdate(TestCorpus.Simple("1", "alpha beta"));
        index.AddOrUpdate(TestCorpus.Simple("2", "alpha alpha gamma"));
        index.Refresh();

        return index;
    }

    private static double ExpectedScore(double idf, int termFrequency, int fieldLength, double averageLength)
    {
        var denominator = termFrequency + (K1 * (1 - B + (B * fieldLength / averageLength)));
        return idf * termFrequency * (K1 + 1) / denominator;
    }

    [Fact]
    public void InverseDocumentFrequencyMatchesTheSmoothedFormula()
    {
        // ln(1 + (N - df + 0.5) / (df + 0.5))
        Assert.Equal(Math.Log(1 + (0.5 / 2.5)), Bm25Similarity.InverseDocumentFrequency(2, 2), 12);
        Assert.Equal(Math.Log(1 + (1.5 / 1.5)), Bm25Similarity.InverseDocumentFrequency(1, 2), 12);
    }

    [Fact]
    public void IdfStaysPositiveForATermPresentInEveryDocument()
    {
        // The enclosing "1 +" is what prevents a universal term scoring zero or negative.
        Assert.True(Bm25Similarity.InverseDocumentFrequency(1000, 1000) > 0);
    }

    [Fact]
    public void IdfRisesAsATermGetsRarer()
    {
        var common = Bm25Similarity.InverseDocumentFrequency(500, 1000);
        var rare = Bm25Similarity.InverseDocumentFrequency(5, 1000);

        Assert.True(rare > common);
    }

    [Fact]
    public void ScoresMatchValuesComputedByHand()
    {
        var index = BuildTwoDocumentIndex();
        var context = index.CreateContext();

        var outcome = index.Searcher.Search(new SearchExecution
        {
            Query = new TermQuery("body", "alpha"),
            Context = context,
            Size = 10
        });

        var idf = Math.Log(1 + (0.5 / 2.5));
        const double averageLength = 2.5;

        var expectedDoc1 = ExpectedScore(idf, termFrequency: 1, fieldLength: 2, averageLength);
        var expectedDoc2 = ExpectedScore(idf, termFrequency: 2, fieldLength: 3, averageLength);

        var byId = outcome.TopDocs.Hits.ToDictionary(h => h.ExternalId, h => h.Score);

        Assert.Equal(expectedDoc2, byId["2"], 10);
        Assert.Equal(expectedDoc1, byId["1"], 10);

        // Two occurrences beat one, even though the containing field is longer.
        Assert.Equal("2", outcome.TopDocs.Hits[0].ExternalId);
    }

    [Fact]
    public void RarerTermOutranksCommonOneForTheSameDocument()
    {
        var index = BuildTwoDocumentIndex();
        var context = index.CreateContext();

        double ScoreOf(string term) => index.Searcher
            .Search(new SearchExecution { Query = new TermQuery("body", term), Context = context, Size = 10 })
            .TopDocs.Hits.Single(h => h.ExternalId == "1").Score;

        // "beta" appears in one of two documents, "alpha" in both.
        Assert.True(ScoreOf("beta") > ScoreOf("alpha"));
    }

    [Fact]
    public void TermFrequencySaturates()
    {
        var similarity = new Bm25Similarity();

        var one = similarity.TermFrequencyFactor(1, 10, 10);
        var ten = similarity.TermFrequencyFactor(10, 10, 10);
        var hundred = similarity.TermFrequencyFactor(100, 10, 10);

        Assert.True(ten > one);
        Assert.True(hundred > ten);

        // The gain from 10 to 100 occurrences is far smaller than from 1 to 10.
        Assert.True(hundred - ten < ten - one);

        // And the factor never exceeds its limit of k1 + 1.
        Assert.True(hundred < 1.2 + 1);
    }

    [Fact]
    public void LongerFieldsScoreLowerForTheSameTermFrequency()
    {
        var similarity = new Bm25Similarity();

        var shortField = similarity.TermFrequencyFactor(1, 5, 10);
        var longField = similarity.TermFrequencyFactor(1, 40, 10);

        Assert.True(shortField > longField);
    }

    [Fact]
    public void MaxScoreIsNeverBelowAnyRealScore()
    {
        // WAND's correctness depends entirely on this bound holding.
        var similarity = new Bm25Similarity();
        var idf = Bm25Similarity.InverseDocumentFrequency(3, 100);
        var bound = similarity.MaxScore(idf);

        foreach (var termFrequency in new[] { 1, 2, 5, 20, 1000 })
        {
            foreach (var length in new[] { 1, 2, 10, 500 })
            {
                Assert.True(
                    similarity.Score(idf, termFrequency, length, 10) <= bound,
                    $"tf={termFrequency} len={length} exceeded the upper bound.");
            }
        }
    }
}
