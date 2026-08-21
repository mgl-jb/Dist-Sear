namespace DistSear.Index.Query;

/// <summary>
/// Collects the exact terms a query will look up, by walking the parsed tree.
///
/// Used by the coordinator's global-statistics pre-pass: it needs to know which document
/// frequencies to gather from every shard *before* any shard scores anything. Multi-term queries
/// are skipped deliberately — their expansion depends on each shard's dictionary, so their
/// statistics cannot be agreed globally in advance, and they fall back to per-shard scoring.
/// </summary>
public static class QueryTerms
{
    public static IReadOnlySet<(string Field, string Term)> Collect(Query query)
    {
        var terms = new HashSet<(string Field, string Term)>();
        Walk(query, terms);

        return terms;
    }

    private static void Walk(Query query, HashSet<(string Field, string Term)> terms)
    {
        switch (query)
        {
            case TermQuery term:
                terms.Add((term.Field, term.Term));
                break;

            case PhraseQuery phrase:
                foreach (var value in phrase.Terms)
                {
                    terms.Add((phrase.Field, value));
                }

                break;

            case TermsQuery explicitTerms:
                foreach (var value in explicitTerms.Values)
                {
                    terms.Add((explicitTerms.Field, value));
                }

                break;
        }

        foreach (var child in query.Children)
        {
            Walk(child, terms);
        }
    }
}
