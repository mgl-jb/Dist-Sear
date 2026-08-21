using Porter2Stemmer;

namespace DistSear.Analysis.Filters;

/// <summary>
/// Reduces English words to a common stem so that "searching", "searches" and "searched" all match
/// the query "search". Uses the Porter2 (Snowball English) algorithm.
/// </summary>
public sealed class PorterStemFilter : ITokenFilter
{
    private readonly EnglishPorter2Stemmer _stemmer = new();

    public IEnumerable<Token> Filter(IEnumerable<Token> tokens)
    {
        foreach (var token in tokens)
        {
            // The stemmer is not thread-safe for concurrent calls, but a filter instance is only
            // ever driven by a single analysis pass at a time.
            var stem = _stemmer.Stem(token.Term).Value;
            yield return string.IsNullOrEmpty(stem) ? token : token.WithTerm(stem);
        }
    }
}
