namespace DistSear.Analysis;

/// <summary>Splits raw text into an initial token stream carrying offsets and positions.</summary>
public interface ITokenizer
{
    IEnumerable<Token> Tokenize(string text);
}

/// <summary>Transforms a token stream. Filters compose in declaration order.</summary>
public interface ITokenFilter
{
    IEnumerable<Token> Filter(IEnumerable<Token> tokens);
}
