namespace DistSear.Analysis.Filters;

/// <summary>
/// Emits every leading prefix of a term between the configured lengths, so a stored term can be
/// found by what the user has typed so far. Index-time only: applying it to the query as well would
/// match a prefix of the prefix.
/// </summary>
public sealed class EdgeNGramFilter : ITokenFilter
{
    private readonly int _minGram;
    private readonly int _maxGram;

    public EdgeNGramFilter(int minGram = 2, int maxGram = 20)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(minGram, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxGram, minGram);

        _minGram = minGram;
        _maxGram = maxGram;
    }

    public IEnumerable<Token> Filter(IEnumerable<Token> tokens)
    {
        foreach (var token in tokens)
        {
            var limit = Math.Min(_maxGram, token.Term.Length);

            for (var length = _minGram; length <= limit; length++)
            {
                yield return token.WithTerm(token.Term[..length]);
            }
        }
    }
}
