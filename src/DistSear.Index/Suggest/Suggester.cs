using DistSear.Abstractions.Mapping;
using DistSear.Abstractions.Search;
using DistSear.Analysis;
using DistSear.Analysis.Filters;
using DistSear.Index.Query;
using DistSear.Index.Segments;

namespace DistSear.Index.Suggest;

/// <summary>
/// Typeahead completion, with did-you-mean correction as a fallback.
///
/// Completion reuses the inverted index rather than building a parallel structure: a suggest field
/// is indexed through the edge-n-gram chain, so every prefix of every word is already a term. What
/// the user has typed is therefore a single term lookup, and the original text comes back from
/// stored fields.
///
/// Correction is a different problem — the user typed something that is not a prefix of anything —
/// so it walks the term dictionary with the Levenshtein automaton and prefers the correction that
/// appears in the most documents.
/// </summary>
public sealed class Suggester
{
    private readonly ShardIndex _index;

    public Suggester(ShardIndex index) => _index = index;

    public SuggestResponse Suggest(
        SuggestRequest request,
        IReadOnlyCollection<string>? principals = null,
        CancellationToken cancellationToken = default)
    {
        var field = _index.Mapping.Require(request.Field);

        if (!field.Suggest)
        {
            throw new InvalidOperationException(
                $"Field '{field.Name}' is not mapped for suggestions (set Suggest = true).");
        }

        var prefix = Normalise(request.Text);

        if (prefix.Length == 0)
        {
            return Empty();
        }

        var completions = Complete(field, prefix, request, principals, cancellationToken);

        if (completions.Count > 0 || !request.AllowCorrection)
        {
            return new SuggestResponse
            {
                Suggestions = completions,
                Corrected = false,
                Shards = SingleShard()
            };
        }

        return new SuggestResponse
        {
            Suggestions = Correct(prefix, request.Size),
            Corrected = true,
            Shards = SingleShard()
        };
    }

    private List<Suggestion> Complete(
        FieldMapping field,
        string prefix,
        SuggestRequest request,
        IReadOnlyCollection<string>? principals,
        CancellationToken cancellationToken)
    {
        // Over-fetch: several documents can share a completion, and duplicates collapse below.
        var candidateCount = Math.Max(request.Size * 5, 20);

        var outcome = _index.Searcher.Search(
            new SearchExecution
            {
                Query = new TermQuery(field.Name, prefix),
                Context = _index.CreateContext(),
                Size = candidateCount,
                Principals = principals
            },
            cancellationToken);

        var weights = new Dictionary<string, (double Score, long Count)>(StringComparer.Ordinal);

        foreach (var hit in outcome.TopDocs.Hits)
        {
            var stored = _index.GetStoredFields(hit.ExternalId);

            if (stored is null || !stored.TryGetValue(field.Name, out var raw) || raw is null)
            {
                continue;
            }

            foreach (var value in FieldCoercion.Flatten(raw))
            {
                var text = value as string ?? value.ToString();

                if (string.IsNullOrEmpty(text) || !StartsWithPrefix(text, prefix))
                {
                    continue;
                }

                var existing = weights.GetValueOrDefault(text);
                weights[text] = (Math.Max(existing.Score, hit.Score), existing.Count + 1);
            }
        }

        return
        [
            .. weights
                .OrderByDescending(kv => kv.Value.Count)
                .ThenByDescending(kv => kv.Value.Score)
                .ThenBy(kv => kv.Key.Length)
                .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                .Take(request.Size)
                .Select(kv => new Suggestion(kv.Key, kv.Value.Score, kv.Value.Count))
        ];
    }

    /// <summary>
    /// Did-you-mean. Scans the default text field's dictionary for terms within two edits and
    /// prefers the one appearing in most documents, on the reasoning that the common spelling is
    /// the intended one.
    /// </summary>
    private List<Suggestion> Correct(string text, int size)
    {
        var field = _index.Mapping.DefaultField;
        var automaton = new LevenshteinAutomaton(text, MaxCorrectionEdits(text));
        var candidates = new List<Suggestion>();

        foreach (var segment in _index.Searcher.Segments)
        {
            if (segment.GetField(field) is not { } terms)
            {
                continue;
            }

            foreach (var term in terms.SortedTerms)
            {
                var state = automaton.Start();
                var viable = true;

                foreach (var character in term)
                {
                    state = automaton.Step(in state, character);

                    if (!automaton.CanMatch(in state))
                    {
                        viable = false;
                        break;
                    }
                }

                if (!viable || !automaton.IsMatch(in state))
                {
                    continue;
                }

                var frequency = terms.DocumentFrequency(term);
                var distance = automaton.Distance(in state);

                // Closeness first, popularity second.
                candidates.Add(new Suggestion(term, 1.0 / (1 + distance), frequency));
            }
        }

        return
        [
            .. candidates
                .GroupBy(c => c.Text, StringComparer.Ordinal)
                .Select(g => new Suggestion(g.Key, g.Max(c => c.Score), g.Sum(c => c.Frequency)))
                .OrderByDescending(c => c.Score)
                .ThenByDescending(c => c.Frequency)
                .ThenBy(c => c.Text, StringComparer.Ordinal)
                .Take(size)
        ];
    }

    /// <summary>Short words tolerate fewer edits, or every three-letter word matches every other.</summary>
    private static int MaxCorrectionEdits(string text) => text.Length switch
    {
        <= 2 => 0,
        <= 4 => 1,
        _ => 2
    };

    private static bool StartsWithPrefix(string text, string prefix) =>
        Normalise(text).StartsWith(prefix, StringComparison.Ordinal)
        || Normalise(text).Split(' ').Any(word => word.StartsWith(prefix, StringComparison.Ordinal));

    /// <summary>
    /// Mirrors the front of the suggest analyzer: lowercase and fold, but do not expand into
    /// n-grams. What the user typed is the prefix itself, not something to take prefixes of.
    /// </summary>
    private static string Normalise(string text) =>
        AsciiFoldingFilter.Fold(text.Trim().ToLowerInvariant());

    private SuggestResponse Empty() => new()
    {
        Suggestions = [],
        Corrected = false,
        Shards = SingleShard()
    };

    private ShardStatistics SingleShard() => new() { Total = 1, Successful = 1 };
}
