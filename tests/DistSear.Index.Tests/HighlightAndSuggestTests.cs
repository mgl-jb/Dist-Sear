using DistSear.Abstractions.Documents;
using DistSear.Abstractions.Mapping;
using DistSear.Abstractions.Search;
using DistSear.Analysis;
using DistSear.Index;
using DistSear.Index.Highlight;
using DistSear.Index.Query;
using DistSear.Index.Suggest;
using Xunit;

namespace DistSear.Index.Tests;

public class HighlighterTests
{
    private readonly AnalyzerRegistry _analyzers = new();
    private readonly Highlighter _highlighter;
    private readonly QueryParser _parser;

    private static readonly FieldMapping Body = new()
    {
        Name = "body",
        Type = FieldType.Text,
        Analyzer = AnalyzerRegistry.English
    };

    public HighlighterTests()
    {
        _highlighter = new Highlighter(_analyzers);
        _parser = new QueryParser(TestCorpus.ProductMapping, _analyzers);
    }

    private IReadOnlyList<string> Highlight(string text, string query, HighlightSpec? spec = null)
    {
        var matchers = HighlightTermExtractor.Extract(_parser.Parse(query));

        return _highlighter.Highlight(
            Body,
            text,
            matchers,
            spec ?? new HighlightSpec { Fields = ["body"] });
    }

    [Fact]
    public void MarksUpTheMatchedWord()
    {
        var passages = Highlight("A guide to fast search systems", "body:search");

        Assert.Single(passages);
        Assert.Contains("<em>search</em>", passages[0]);
    }

    [Fact]
    public void HighlightsTheOriginalWordFormNotItsStem()
    {
        // The query stems to "search"; the text says "searches". The snippet must show what the
        // author wrote, with the inflected form marked up.
        var passages = Highlight("The engine searches every document", "body:searching");

        Assert.Contains("<em>searches</em>", passages[0]);
    }

    [Fact]
    public void PreservesSurroundingTextExactly()
    {
        var passages = Highlight("Alpha beta gamma", "body:beta");

        Assert.Equal("Alpha <em>beta</em> gamma", passages[0]);
    }

    [Fact]
    public void CustomTagsAreUsed()
    {
        var passages = Highlight(
            "Alpha beta gamma",
            "body:beta",
            new HighlightSpec { Fields = ["body"], PreTag = "[[", PostTag = "]]" });

        Assert.Equal("Alpha [[beta]] gamma", passages[0]);
    }

    [Fact]
    public void ReturnsNothingWhenTheFieldDoesNotMatch() =>
        Assert.Empty(Highlight("Alpha beta gamma", "body:delta"));

    [Fact]
    public void ProhibitedTermsAreNeverHighlighted()
    {
        // "gamma" is excluded from the query, so it is not a reason the document matched.
        var passages = Highlight("Alpha beta gamma", "body:beta -body:gamma");

        Assert.Contains("<em>beta</em>", passages[0]);
        Assert.DoesNotContain("<em>gamma</em>", passages[0]);
    }

    [Fact]
    public void PrefixQueriesHighlightEveryExpansion()
    {
        var passages = Highlight("searching searchable seagull", "body:sea*");

        Assert.Contains("<em>searching</em>", passages[0]);
        Assert.Contains("<em>seagull</em>", passages[0]);
    }

    [Fact]
    public void FuzzyQueriesHighlightTheNearMiss()
    {
        var passages = Highlight("the search engine", "body:serch~1");

        Assert.Contains("<em>search</em>", passages[0]);
    }

    [Fact]
    public void PhraseTermsAreAllHighlighted()
    {
        var passages = Highlight("a fast distributed search engine", "body:\"distributed search\"");

        Assert.Contains("<em>distributed</em>", passages[0]);
        Assert.Contains("<em>search</em>", passages[0]);
    }

    [Fact]
    public void LongTextIsSplitIntoBoundedPassages()
    {
        var filler = string.Join(' ', Enumerable.Repeat("padding", 60));
        var text = $"first search {filler} second search";

        var passages = Highlight(
            text,
            "body:search",
            new HighlightSpec { Fields = ["body"], FragmentSize = 60, MaxFragments = 5 });

        Assert.Equal(2, passages.Count);
        Assert.All(passages, p => Assert.Contains("<em>search</em>", p));

        // Each snippet stays near the requested size rather than returning the whole document.
        Assert.All(passages, p => Assert.True(p.Length < 200, $"Passage was {p.Length} characters."));
    }

    [Fact]
    public void PassageCountIsCapped()
    {
        var text = string.Join(' ', Enumerable.Repeat("search padding padding padding padding", 20));

        var passages = Highlight(
            text,
            "body:search",
            new HighlightSpec { Fields = ["body"], FragmentSize = 40, MaxFragments = 2 });

        Assert.Equal(2, passages.Count);
    }

    [Fact]
    public void EmptyTextYieldsNoPassages() =>
        Assert.Empty(Highlight("", "body:search"));
}

public class SuggesterTests
{
    private static readonly IndexMapping Mapping = new(
        "catalog",
        [
            new FieldMapping
            {
                Name = "name",
                Type = FieldType.Text,
                Analyzer = AnalyzerRegistry.Suggest,
                SearchAnalyzer = AnalyzerRegistry.Simple,
                Suggest = true,
                Stored = true
            },
            new FieldMapping { Name = "body", Type = FieldType.Text, Analyzer = AnalyzerRegistry.Simple }
        ],
        defaultField: "body");

    private readonly ShardIndex _index;
    private readonly Suggester _suggester;

    public SuggesterTests()
    {
        _index = new ShardIndex(Mapping, new AnalyzerRegistry());

        var entries = new[]
        {
            ("1", "Espresso machine", "coffee brewing equipment"),
            ("2", "Espresso grinder", "coffee grinding equipment"),
            ("3", "Espresso tamper", "coffee tamping tool"),
            ("4", "Electric kettle", "boiling water quickly"),
            ("5", "French press", "coffee steeping")
        };

        foreach (var (id, name, body) in entries)
        {
            _index.AddOrUpdate(new IndexedDocument
            {
                Id = id,
                ShardKey = "shard-0",
                Fields = new Dictionary<string, object?> { ["name"] = name, ["body"] = body }
            });
        }

        _index.Refresh();
        _suggester = new Suggester(_index);
    }

    private SuggestResponse Suggest(string text, int size = 5) =>
        _suggester.Suggest(new SuggestRequest { Index = "catalog", Field = "name", Text = text, Size = size });

    [Fact]
    public void CompletesFromAShortPrefix()
    {
        var response = Suggest("esp");

        Assert.False(response.Corrected);
        Assert.Equal(3, response.Suggestions.Count);
        Assert.All(response.Suggestions, s => Assert.StartsWith("Espresso", s.Text));
    }

    [Fact]
    public void CompletesOnAnInteriorWordToo()
    {
        // "grin" is a prefix of the second word, which is what a user typing mid-phrase expects.
        var response = Suggest("grin");

        Assert.Contains(response.Suggestions, s => s.Text == "Espresso grinder");
    }

    [Fact]
    public void CompletionIsCaseAndAccentInsensitive()
    {
        Assert.NotEmpty(Suggest("ESP").Suggestions);
        Assert.NotEmpty(Suggest("Esp").Suggestions);
    }

    [Fact]
    public void RespectsTheRequestedSize() =>
        Assert.Equal(2, Suggest("esp", size: 2).Suggestions.Count);

    [Fact]
    public void FallsBackToCorrectionWhenNothingCompletes()
    {
        // "coffe" completes nothing in the name field, so correction takes over on the body field.
        var response = Suggest("coffe");

        Assert.True(response.Corrected);
        Assert.Contains(response.Suggestions, s => s.Text == "coffee");
    }

    [Fact]
    public void CorrectionPrefersTheMoreCommonSpelling()
    {
        var response = Suggest("coffe");
        var top = response.Suggestions[0];

        Assert.Equal("coffee", top.Text);
        Assert.Equal(4, top.Frequency);
    }

    [Fact]
    public void CorrectionCanBeDisabled()
    {
        var response = _suggester.Suggest(new SuggestRequest
        {
            Index = "catalog",
            Field = "name",
            Text = "coffe",
            AllowCorrection = false
        });

        Assert.False(response.Corrected);
        Assert.Empty(response.Suggestions);
    }

    [Fact]
    public void EmptyInputSuggestsNothing() =>
        Assert.Empty(Suggest("   ").Suggestions);

    [Fact]
    public void UnmappedSuggestFieldIsRejected()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            _suggester.Suggest(new SuggestRequest { Index = "catalog", Field = "body", Text = "cof" }));

        Assert.Contains("not mapped for suggestions", error.Message);
    }
}
