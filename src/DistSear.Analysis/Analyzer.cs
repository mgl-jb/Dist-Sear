namespace DistSear.Analysis;

/// <summary>
/// A tokenizer plus an ordered filter chain. Analysis is lazy end to end: nothing is materialised
/// unless the caller enumerates, so indexing a large field does not allocate an intermediate list
/// per stage.
/// </summary>
public sealed class Analyzer
{
    private readonly ITokenizer _tokenizer;
    private readonly IReadOnlyList<ITokenFilter> _filters;

    public Analyzer(string name, ITokenizer tokenizer, params ITokenFilter[] filters)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        Name = name;
        _tokenizer = tokenizer;
        _filters = filters;
    }

    public string Name { get; }

    public IEnumerable<Token> Analyze(string text)
    {
        var stream = _tokenizer.Tokenize(text);

        foreach (var filter in _filters)
        {
            stream = filter.Filter(stream);
        }

        return stream;
    }

    /// <summary>Convenience for callers that only need the term strings, such as query parsing.</summary>
    public List<string> AnalyzeToTerms(string text) =>
        Analyze(text).Select(t => t.Term).ToList();
}
