namespace DistSear.Analysis.Filters;

/// <summary>Case-folds every term so that queries match regardless of the case used.</summary>
public sealed class LowercaseFilter : ITokenFilter
{
    public static LowercaseFilter Instance { get; } = new();

    public IEnumerable<Token> Filter(IEnumerable<Token> tokens)
    {
        foreach (var token in tokens)
        {
            yield return token.WithTerm(token.Term.ToLowerInvariant());
        }
    }
}
