using System.Collections.Frozen;

namespace DistSear.Analysis.Filters;

/// <summary>
/// Expands a term into its equivalents, all emitted at the same position as the original so that
/// phrase queries still line up. Apply at index time only, or at query time only — doing both
/// squares the expansion for no benefit.
/// </summary>
public sealed class SynonymFilter : ITokenFilter
{
    private readonly FrozenDictionary<string, string[]> _map;

    private SynonymFilter(FrozenDictionary<string, string[]> map) => _map = map;

    /// <summary>
    /// Builds a filter from Solr-style equivalence rules, one group per line, e.g.
    /// <c>tv, television, telly</c>. Every member of a group expands to every other member.
    /// </summary>
    public static SynonymFilter FromRules(IEnumerable<string> rules)
    {
        var builder = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var rule in rules)
        {
            if (string.IsNullOrWhiteSpace(rule) || rule.StartsWith('#'))
            {
                continue;
            }

            var members = rule
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(m => m.ToLowerInvariant())
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            if (members.Length < 2)
            {
                continue;
            }

            foreach (var member in members)
            {
                if (!builder.TryGetValue(member, out var set))
                {
                    set = new HashSet<string>(StringComparer.Ordinal);
                    builder[member] = set;
                }

                foreach (var other in members)
                {
                    if (!string.Equals(other, member, StringComparison.Ordinal))
                    {
                        set.Add(other);
                    }
                }
            }
        }

        return new SynonymFilter(
            builder.ToFrozenDictionary(kv => kv.Key, kv => kv.Value.ToArray(), StringComparer.Ordinal));
    }

    public IEnumerable<Token> Filter(IEnumerable<Token> tokens)
    {
        foreach (var token in tokens)
        {
            yield return token;

            if (_map.TryGetValue(token.Term, out var synonyms))
            {
                foreach (var synonym in synonyms)
                {
                    yield return token.WithTerm(synonym);
                }
            }
        }
    }
}
