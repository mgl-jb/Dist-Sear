using DistSear.Abstractions.Mapping;
using DistSear.Analysis;
using Xunit;

namespace DistSear.Analysis.Tests;

public class AnalyzerRegistryTests
{
    private readonly AnalyzerRegistry _registry = new();

    [Fact]
    public void StandardAnalyzerLowercasesFoldsAndRemovesStopwords()
    {
        var terms = _registry.Get(AnalyzerRegistry.Standard)
            .AnalyzeToTerms("The Naïve SEARCH of a Fox");

        Assert.Equal(["naive", "search", "fox"], terms);
    }

    [Fact]
    public void EnglishAnalyzerAlsoStems()
    {
        var terms = _registry.Get(AnalyzerRegistry.English)
            .AnalyzeToTerms("Distributed searching systems");

        Assert.Equal(["distribut", "search", "system"], terms);
    }

    [Fact]
    public void KeywordAnalyzerDoesNotSplitOrFold()
    {
        var terms = _registry.Get(AnalyzerRegistry.Keyword).AnalyzeToTerms("Home & Garden");

        Assert.Equal(["Home & Garden"], terms);
    }

    [Fact]
    public void KeywordFieldsBypassTheConfiguredTextAnalyzer()
    {
        var field = new FieldMapping
        {
            Name = "category",
            Type = FieldType.Keyword,
            Analyzer = AnalyzerRegistry.English
        };

        Assert.Equal(AnalyzerRegistry.Keyword, _registry.ForIndexing(field).Name);
        Assert.Equal(AnalyzerRegistry.Keyword, _registry.ForSearching(field).Name);
    }

    [Fact]
    public void SearchAnalyzerCanDifferFromTheIndexAnalyzer()
    {
        var field = new FieldMapping
        {
            Name = "title",
            Type = FieldType.Text,
            Analyzer = AnalyzerRegistry.Suggest,
            SearchAnalyzer = AnalyzerRegistry.Simple
        };

        // Index time expands to prefixes; search time must not, or a prefix of the prefix matches.
        Assert.Equal(AnalyzerRegistry.Suggest, _registry.ForIndexing(field).Name);
        Assert.Equal(AnalyzerRegistry.Simple, _registry.ForSearching(field).Name);
    }

    [Fact]
    public void UnknownAnalyzerNameFailsLoudly() =>
        Assert.Throws<KeyNotFoundException>(() => _registry.Get("nope"));

    [Fact]
    public void SynonymAnalyzerIsRegisteredWhenRulesAreSupplied()
    {
        var registry = new AnalyzerRegistry(["tv, television"]);
        var terms = registry.Get("synonym").AnalyzeToTerms("cheap tv");

        Assert.Contains("televis", terms);
    }
}
