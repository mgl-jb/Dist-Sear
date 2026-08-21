using DistSear.Index.Query;

namespace DistSear.Index.Highlight;

/// <summary>Decides whether an analyzed term from the stored text should be marked up.</summary>
public interface IHighlightMatcher
{
    string Field { get; }

    bool Matches(string term);
}

internal sealed record ExactMatcher(string Field, string Term) : IHighlightMatcher
{
    public bool Matches(string term) => string.Equals(term, Term, StringComparison.Ordinal);
}

internal sealed record PrefixMatcher(string Field, string Prefix) : IHighlightMatcher
{
    public bool Matches(string term) => term.StartsWith(Prefix, StringComparison.Ordinal);
}

internal sealed record WildcardMatcher(string Field, string Pattern) : IHighlightMatcher
{
    public bool Matches(string term) => WildcardQuery.Matches(Pattern, term);
}

internal sealed class FuzzyMatcher : IHighlightMatcher
{
    private readonly LevenshteinAutomaton _automaton;

    public FuzzyMatcher(string field, string term, int maxEdits)
    {
        Field = field;
        _automaton = new LevenshteinAutomaton(term, maxEdits);
    }

    public string Field { get; }

    public bool Matches(string term)
    {
        var state = _automaton.Start();

        foreach (var character in term)
        {
            state = _automaton.Step(in state, character);

            if (!_automaton.CanMatch(in state))
            {
                return false;
            }
        }

        return _automaton.IsMatch(in state);
    }
}

/// <summary>
/// Walks a query tree collecting the matchers a highlighter should apply.
///
/// This works from the query's structure rather than from the terms a weight resolved, because
/// multi-term queries expand differently in every segment — and highlighting has to mark up the
/// text the user is about to read, not the postings the scorer happened to visit.
/// </summary>
public static class HighlightTermExtractor
{
    public static IReadOnlyList<IHighlightMatcher> Extract(Query.Query query)
    {
        var matchers = new List<IHighlightMatcher>();
        Walk(query, matchers);

        return matchers;
    }

    private static void Walk(Query.Query query, List<IHighlightMatcher> matchers)
    {
        switch (query)
        {
            case TermQuery term:
                matchers.Add(new ExactMatcher(term.Field, term.Term));
                break;

            case PhraseQuery phrase:
                foreach (var term in phrase.Terms)
                {
                    matchers.Add(new ExactMatcher(phrase.Field, term));
                }

                break;

            case PrefixQuery prefix:
                matchers.Add(new PrefixMatcher(prefix.Field, prefix.Prefix));
                break;

            case WildcardQuery wildcard:
                matchers.Add(new WildcardMatcher(wildcard.Field, wildcard.Pattern));
                break;

            case FuzzyQuery fuzzy:
                matchers.Add(new FuzzyMatcher(fuzzy.Field, fuzzy.Term, fuzzy.MaxEdits));
                break;

            case TermsQuery terms:
                foreach (var value in terms.Values)
                {
                    matchers.Add(new ExactMatcher(terms.Field, value));
                }

                break;

            // Range queries match on values, not on words, so there is nothing to mark up.
        }

        foreach (var child in query.Children)
        {
            Walk(child, matchers);
        }
    }
}
