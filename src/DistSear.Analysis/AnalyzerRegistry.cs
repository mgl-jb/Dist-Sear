using DistSear.Analysis.Filters;
using DistSear.Abstractions.Mapping;

namespace DistSear.Analysis;

/// <summary>
/// Resolves analyzer names from an index mapping to concrete chains, and supplies the built-ins.
/// </summary>
public sealed class AnalyzerRegistry
{
    public const string Standard = "standard";
    public const string Keyword = "keyword";
    public const string Simple = "simple";
    public const string English = "english";
    public const string Suggest = "suggest";

    private readonly Dictionary<string, Analyzer> _analyzers = new(StringComparer.OrdinalIgnoreCase);

    public AnalyzerRegistry(IEnumerable<string>? synonymRules = null)
    {
        // Verbatim: the whole value becomes one term. Filters, sorts and facets rely on this.
        Register(new Analyzer(Keyword, KeywordTokenizer.Instance));

        // Case-folded words, nothing else. Useful when stemming would be wrong (names, codes).
        Register(new Analyzer(Simple, StandardTokenizer.Instance, LowercaseFilter.Instance));

        // The default for text fields.
        Register(new Analyzer(
            Standard,
            StandardTokenizer.Instance,
            LowercaseFilter.Instance,
            AsciiFoldingFilter.Instance,
            StopwordFilter.English));

        // Adds stemming, so inflected forms of a word conflate.
        Register(new Analyzer(
            English,
            StandardTokenizer.Instance,
            LowercaseFilter.Instance,
            AsciiFoldingFilter.Instance,
            StopwordFilter.English,
            new PorterStemFilter()));

        // Index-time chain for completion fields. The matching search-time analyzer is "simple":
        // the query is a literal prefix and must not itself be expanded into prefixes.
        Register(new Analyzer(
            Suggest,
            StandardTokenizer.Instance,
            LowercaseFilter.Instance,
            AsciiFoldingFilter.Instance,
            new EdgeNGramFilter(1, 20)));

        if (synonymRules is not null)
        {
            Register(new Analyzer(
                "synonym",
                StandardTokenizer.Instance,
                LowercaseFilter.Instance,
                AsciiFoldingFilter.Instance,
                StopwordFilter.English,
                SynonymFilter.FromRules(synonymRules),
                new PorterStemFilter()));
        }
    }

    public void Register(Analyzer analyzer) => _analyzers[analyzer.Name] = analyzer;

    public Analyzer Get(string name) =>
        _analyzers.TryGetValue(name, out var analyzer)
            ? analyzer
            : throw new KeyNotFoundException($"Analyzer '{name}' is not registered.");

    public bool Has(string name) => _analyzers.ContainsKey(name);

    /// <summary>Index-time analyzer for a mapped field. Non-text fields are never analyzed.</summary>
    public Analyzer ForIndexing(FieldMapping field) =>
        field.Type == FieldType.Text ? Get(field.Analyzer) : Get(Keyword);

    /// <summary>Search-time analyzer for a mapped field.</summary>
    public Analyzer ForSearching(FieldMapping field) =>
        field.Type == FieldType.Text ? Get(field.EffectiveSearchAnalyzer) : Get(Keyword);
}
