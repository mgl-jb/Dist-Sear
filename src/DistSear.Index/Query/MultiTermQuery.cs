using DistSear.Index.Scoring;
using DistSear.Index.Segments;

namespace DistSear.Index.Query;

/// <summary>How a query that expands into many terms should score.</summary>
public enum MultiTermScoreMode
{
    /// <summary>All matches score identically. Right for prefix and wildcard, which express
    /// membership rather than relevance, and avoids rare expansions dominating the ranking.</summary>
    ConstantScore,

    /// <summary>Each expanded term scores normally, weighted by how well it matched.</summary>
    Scored
}

/// <summary>
/// Base for queries that resolve to a set of terms only once a segment's dictionary is available:
/// prefix, wildcard, fuzzy and explicit term-set queries.
/// </summary>
public abstract class MultiTermQuery : Query
{
    protected MultiTermQuery(string field, MultiTermScoreMode scoreMode)
    {
        Field = field;
        ScoreMode = scoreMode;
    }

    public string Field { get; }

    public MultiTermScoreMode ScoreMode { get; }

    /// <summary>
    /// Terms in this segment that the query matches, with a per-term weight. Implementations must
    /// stop once <paramref name="maxExpansions"/> terms have been produced.
    /// </summary>
    protected internal abstract IEnumerable<(string Term, double Weight)> Expand(
        FieldTerms field,
        int maxExpansions);

    public override Weight CreateWeight(SearchContext context, double boost) =>
        new MultiTermWeight(this, context, boost);

    private sealed class MultiTermWeight : Weight
    {
        private readonly SearchContext _context;
        private readonly double _boost;

        public MultiTermWeight(MultiTermQuery query, SearchContext context, double boost)
            : base(query)
        {
            _context = context;
            _boost = boost;
        }

        private new MultiTermQuery Query => (MultiTermQuery)base.Query;

        public override Scorer? CreateScorer(Segment segment)
        {
            var field = segment.GetField(Query.Field);

            if (field is null)
            {
                return null;
            }

            var fieldBoost = _context.Mapping.Get(Query.Field)?.Boost ?? 1.0;
            var average = _context.Statistics.AverageFieldLength(Query.Field);
            var scorers = new List<Scorer>();

            foreach (var (term, weight) in Query.Expand(field, _context.MaxExpansions))
            {
                var idf = Bm25Similarity.InverseDocumentFrequency(
                    _context.Statistics.DocumentFrequency(Query.Field, term),
                    _context.Statistics.DocumentCount);

                var scorer = TermQuery.CreateTermScorer(
                    segment,
                    Query.Field,
                    term,
                    _context.Similarity,
                    idf,
                    average,
                    _boost * fieldBoost * weight);

                if (scorer is not null)
                {
                    scorers.Add(scorer);
                }
            }

            if (scorers.Count == 0)
            {
                return null;
            }

            var combined = scorers.Count == 1 ? scorers[0] : new DisjunctionScorer(scorers);

            // Under constant scoring the number of expansions must not leak into the score: a term
            // matching five expansions is no more relevant than one matching a single expansion.
            return Query.ScoreMode == MultiTermScoreMode.ConstantScore
                ? new ConstantScoreScorer(combined, _boost * fieldBoost)
                : combined;
        }
    }
}

/// <summary>Matches every term starting with a prefix. Seeks straight to the prefix in the sorted
/// dictionary rather than scanning the whole field.</summary>
public sealed class PrefixQuery : MultiTermQuery
{
    public PrefixQuery(string field, string prefix)
        : base(field, MultiTermScoreMode.ConstantScore) => Prefix = prefix;

    public string Prefix { get; }

    protected internal override IEnumerable<(string Term, double Weight)> Expand(
        FieldTerms field,
        int maxExpansions) =>
        field.TermsWithPrefix(Prefix).Take(maxExpansions).Select(term => (term, 1.0));

    public override string Describe() => $"{Field}:{Prefix}*";
}

/// <summary>Explicit set of terms, any of which matches. Used for structured filters and, notably,
/// the mandatory document-level security clause.</summary>
public sealed class TermsQuery : MultiTermQuery
{
    public TermsQuery(string field, IReadOnlyList<string> terms)
        : base(field, MultiTermScoreMode.ConstantScore) => Values = terms;

    public IReadOnlyList<string> Values { get; }

    protected internal override IEnumerable<(string Term, double Weight)> Expand(
        FieldTerms field,
        int maxExpansions) =>
        Values.Take(maxExpansions).Select(term => (term, 1.0));

    public override string Describe() => $"{Field}:({string.Join(" OR ", Values)})";
}

/// <summary>
/// Glob matching with <c>*</c> (any run) and <c>?</c> (one character). Any literal prefix before the
/// first wildcard is used to seek, so <c>sea*ch</c> examines only terms starting with "sea".
/// </summary>
public sealed class WildcardQuery : MultiTermQuery
{
    public WildcardQuery(string field, string pattern)
        : base(field, MultiTermScoreMode.ConstantScore) => Pattern = pattern;

    public string Pattern { get; }

    protected internal override IEnumerable<(string Term, double Weight)> Expand(
        FieldTerms field,
        int maxExpansions)
    {
        var literalPrefix = LiteralPrefix(Pattern);
        var candidates = literalPrefix.Length > 0 ? field.TermsWithPrefix(literalPrefix) : field.SortedTerms;
        var produced = 0;

        foreach (var term in candidates)
        {
            if (produced >= maxExpansions)
            {
                yield break;
            }

            if (Matches(Pattern, term))
            {
                produced++;
                yield return (term, 1.0);
            }
        }
    }

    internal static string LiteralPrefix(string pattern)
    {
        var end = pattern.IndexOfAny(['*', '?']);
        return end < 0 ? pattern : pattern[..end];
    }

    /// <summary>
    /// Iterative glob match. The backtracking point is remembered rather than recursed on, so a
    /// pathological pattern such as <c>a*a*a*b</c> cannot blow the stack.
    /// </summary>
    internal static bool Matches(string pattern, string text)
    {
        var p = 0;
        var t = 0;
        var starPattern = -1;
        var starText = 0;

        while (t < text.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || pattern[p] == text[t]))
            {
                p++;
                t++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                starPattern = p++;
                starText = t;
            }
            else if (starPattern >= 0)
            {
                // Re-try the last star, letting it consume one more character.
                p = starPattern + 1;
                t = ++starText;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }

        return p == pattern.Length;
    }

    public override string Describe() => $"{Field}:{Pattern}";
}

/// <summary>
/// Matches terms within an edit distance of the pattern. Walks the sorted term dictionary with a
/// bounded Levenshtein automaton, reusing the automaton state across shared prefixes and skipping
/// any prefix that can no longer reach the edit budget.
/// </summary>
public sealed class FuzzyQuery : MultiTermQuery
{
    public FuzzyQuery(string field, string term, int maxEdits = 2, int prefixLength = 0)
        : base(field, MultiTermScoreMode.Scored)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxEdits);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxEdits, 2);
        ArgumentOutOfRangeException.ThrowIfNegative(prefixLength);

        Term = term;
        MaxEdits = maxEdits;
        PrefixLength = prefixLength;
    }

    public string Term { get; }

    public int MaxEdits { get; }

    /// <summary>Leading characters that must match exactly. Narrows the dictionary scan sharply.</summary>
    public int PrefixLength { get; }

    protected internal override IEnumerable<(string Term, double Weight)> Expand(
        FieldTerms field,
        int maxExpansions)
    {
        var prefix = Term[..Math.Min(PrefixLength, Term.Length)];
        var automaton = new LevenshteinAutomaton(Term, MaxEdits);
        var terms = field.SortedTerms;
        var produced = 0;

        // Cache of automaton states by prefix length, so a term sharing k characters with its
        // predecessor only pays for the characters beyond k.
        var states = new List<LevenshteinAutomaton.State> { automaton.Start() };
        var previous = string.Empty;

        for (var i = prefix.Length > 0 ? field.SeekTo(prefix) : 0; i < terms.Length; i++)
        {
            var term = terms[i];

            if (prefix.Length > 0 && !term.StartsWith(prefix, StringComparison.Ordinal))
            {
                yield break;
            }

            if (produced >= maxExpansions)
            {
                yield break;
            }

            var shared = CommonPrefixLength(previous, term);
            states.RemoveRange(shared + 1, states.Count - shared - 1);

            var viable = true;

            for (var c = shared; c < term.Length; c++)
            {
                var next = automaton.Step(states[^1], term[c]);
                states.Add(next);

                if (!automaton.CanMatch(in next))
                {
                    viable = false;
                    break;
                }
            }

            previous = term[..(states.Count - 1)];

            if (!viable)
            {
                continue;
            }

            var state = states[^1];

            if (automaton.IsMatch(in state))
            {
                var distance = automaton.Distance(in state);

                // Closer matches should outrank looser ones rather than all scoring alike.
                var similarity = 1.0 - ((double)distance / Math.Max(Term.Length, 1));

                produced++;
                yield return (term, Math.Max(similarity, 0.1));
            }
        }
    }

    private static int CommonPrefixLength(string a, string b)
    {
        var limit = Math.Min(a.Length, b.Length);
        var i = 0;

        while (i < limit && a[i] == b[i])
        {
            i++;
        }

        return i;
    }

    public override string Describe() => $"{Field}:{Term}~{MaxEdits}";
}
