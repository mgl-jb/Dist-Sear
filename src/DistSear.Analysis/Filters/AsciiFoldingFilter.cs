using System.Globalization;
using System.Text;

namespace DistSear.Analysis.Filters;

/// <summary>
/// Strips diacritics so that "resume" matches "résumé". Works by decomposing to canonical
/// decomposed form and discarding the combining marks, which covers the Latin range without a
/// hand-maintained substitution table.
/// </summary>
public sealed class AsciiFoldingFilter : ITokenFilter
{
    public static AsciiFoldingFilter Instance { get; } = new();

    public IEnumerable<Token> Filter(IEnumerable<Token> tokens)
    {
        foreach (var token in tokens)
        {
            yield return token.WithTerm(Fold(token.Term));
        }
    }

    public static string Fold(string term)
    {
        if (term.All(char.IsAscii))
        {
            return term;
        }

        var decomposed = term.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);

        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(ch);
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }
}
