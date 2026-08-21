using DistSear.Index.Query;
using Xunit;

namespace DistSear.Index.Tests;

public class LevenshteinAutomatonTests
{
    /// <summary>Runs a whole candidate through the automaton and reports the resulting distance.</summary>
    private static (bool Matched, int Distance) Run(string pattern, string candidate, int maxEdits)
    {
        var automaton = new LevenshteinAutomaton(pattern, maxEdits);
        var state = automaton.Start();

        foreach (var character in candidate)
        {
            state = automaton.Step(in state, character);
        }

        return (automaton.IsMatch(in state), automaton.Distance(in state));
    }

    [Theory]
    [InlineData("search", "search", 0)]
    [InlineData("search", "serch", 1)]
    [InlineData("search", "seaarch", 1)]
    [InlineData("search", "searhc", 1)]
    [InlineData("search", "sarch", 1)]
    [InlineData("search", "", 6)]
    [InlineData("", "abc", 3)]
    [InlineData("kitten", "sitting", 3)]
    public void ComputesTheSameDistanceAsTheReferenceImplementation(
        string pattern,
        string candidate,
        int expected)
    {
        Assert.Equal(expected, LevenshteinAutomaton.Distance(pattern, candidate));

        var (_, distance) = Run(pattern, candidate, maxEdits: 10);
        Assert.Equal(expected, distance);
    }

    [Fact]
    public void TranspositionCostsOneEditWhenDamerauIsEnabled()
    {
        Assert.Equal(1, LevenshteinAutomaton.Distance("searhc", "search"));

        // Without transpositions the same swap costs two substitutions.
        Assert.Equal(2, LevenshteinAutomaton.Distance("searhc", "search", transpositions: false));
    }

    [Fact]
    public void AgreesWithTheReferenceAcrossExhaustiveShortStrings()
    {
        var alphabet = "abc";
        var words = new List<string> { "" };

        for (var length = 1; length <= 3; length++)
        {
            words.AddRange(
                words.Where(w => w.Length == length - 1)
                    .SelectMany(w => alphabet.Select(c => w + c))
                    .ToList());
        }

        foreach (var pattern in words)
        {
            foreach (var candidate in words)
            {
                var expected = LevenshteinAutomaton.Distance(pattern, candidate);
                var (_, actual) = Run(pattern, candidate, maxEdits: 10);

                Assert.Equal(expected, actual);
            }
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void MatchExactlyMirrorsTheDistanceBudget(int maxEdits)
    {
        string[] candidates = ["search", "serch", "seaarch", "srch", "totally", "searches"];

        foreach (var candidate in candidates)
        {
            var expected = LevenshteinAutomaton.Distance("search", candidate) <= maxEdits;
            var (matched, _) = Run("search", candidate, maxEdits);

            Assert.Equal(expected, matched);
        }
    }

    [Fact]
    public void CanMatchStaysTrueWhileAPrefixIsStillViable()
    {
        var automaton = new LevenshteinAutomaton("search", maxEdits: 1);
        var state = automaton.Start();

        foreach (var character in "sea")
        {
            state = automaton.Step(in state, character);
            Assert.True(automaton.CanMatch(in state));
        }
    }

    [Fact]
    public void CanMatchGoesFalseOncePrefixDivergesBeyondBudget()
    {
        // "zzz" is already three edits away from anything "search" can become.
        var automaton = new LevenshteinAutomaton("search", maxEdits: 1);
        var state = automaton.Start();

        foreach (var character in "zzzz")
        {
            state = automaton.Step(in state, character);
        }

        Assert.False(automaton.CanMatch(in state));
    }

    [Fact]
    public void PrefixStateCanBeReusedAcrossCandidatesSharingThatPrefix()
    {
        // This is the property the dictionary traversal relies on: the row after consuming "sea"
        // is identical no matter which term supplied those characters.
        var automaton = new LevenshteinAutomaton("search", maxEdits: 2);

        var viaFirst = automaton.Start();
        foreach (var c in "sea") viaFirst = automaton.Step(in viaFirst, c);
        foreach (var c in "rch") viaFirst = automaton.Step(in viaFirst, c);

        var viaSecond = automaton.Start();
        foreach (var c in "search") viaSecond = automaton.Step(in viaSecond, c);

        Assert.Equal(automaton.Distance(in viaSecond), automaton.Distance(in viaFirst));
    }
}
