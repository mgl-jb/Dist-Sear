using System.Collections.Frozen;

namespace DistSear.Analysis.Filters;

/// <summary>
/// Drops very common words. Positions are not renumbered, so the gap left behind still blocks a
/// phrase query from matching across the removed word.
/// </summary>
public sealed class StopwordFilter : ITokenFilter
{
    /// <summary>The standard English stopword set, matching the classic Lucene list.</summary>
    public static FrozenSet<string> EnglishStopwords { get; } = new[]
    {
        "a", "an", "and", "are", "as", "at", "be", "but", "by", "for", "if", "in", "into", "is",
        "it", "no", "not", "of", "on", "or", "such", "that", "the", "their", "then", "there",
        "these", "they", "this", "to", "was", "will", "with"
    }.ToFrozenSet(StringComparer.Ordinal);

    private readonly FrozenSet<string> _stopwords;

    public StopwordFilter(IEnumerable<string>? stopwords = null) =>
        _stopwords = stopwords is null
            ? EnglishStopwords
            : stopwords.Select(w => w.ToLowerInvariant()).ToFrozenSet(StringComparer.Ordinal);

    public static StopwordFilter English { get; } = new();

    public IEnumerable<Token> Filter(IEnumerable<Token> tokens)
    {
        foreach (var token in tokens)
        {
            if (!_stopwords.Contains(token.Term))
            {
                yield return token;
            }
        }
    }
}
