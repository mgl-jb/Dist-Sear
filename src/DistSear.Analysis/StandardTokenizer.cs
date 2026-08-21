using System.Globalization;
using System.Text;

namespace DistSear.Analysis;

/// <summary>
/// Unicode-aware tokenizer that emits maximal runs of letters and digits. Operates on runes rather
/// than chars so that characters outside the Basic Multilingual Plane are not split in half.
/// </summary>
public sealed class StandardTokenizer : ITokenizer
{
    public static StandardTokenizer Instance { get; } = new();

    public IEnumerable<Token> Tokenize(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            yield break;
        }

        var builder = new StringBuilder();
        var position = 0;
        var start = 0;
        var index = 0;

        while (index < text.Length)
        {
            if (!Rune.TryGetRuneAt(text, index, out var rune))
            {
                // Unpaired surrogate: skip the single char and break any run in progress.
                if (builder.Length > 0)
                {
                    yield return new Token(builder.ToString(), position++, start, index);
                    builder.Clear();
                }

                index++;
                continue;
            }

            var width = rune.Utf16SequenceLength;

            if (IsTokenChar(rune))
            {
                if (builder.Length == 0)
                {
                    start = index;
                }

                builder.Append(text, index, width);
            }
            else if (builder.Length > 0)
            {
                yield return new Token(builder.ToString(), position++, start, index);
                builder.Clear();
            }

            index += width;
        }

        if (builder.Length > 0)
        {
            yield return new Token(builder.ToString(), position, start, text.Length);
        }
    }

    private static bool IsTokenChar(Rune rune) =>
        Rune.GetUnicodeCategory(rune) switch
        {
            UnicodeCategory.UppercaseLetter or
            UnicodeCategory.LowercaseLetter or
            UnicodeCategory.TitlecaseLetter or
            UnicodeCategory.ModifierLetter or
            UnicodeCategory.OtherLetter or
            UnicodeCategory.DecimalDigitNumber or
            UnicodeCategory.LetterNumber or
            UnicodeCategory.OtherNumber or
            UnicodeCategory.NonSpacingMark => true,
            _ => false
        };
}

/// <summary>Emits the entire input as a single token. Used for <c>keyword</c> fields.</summary>
public sealed class KeywordTokenizer : ITokenizer
{
    public static KeywordTokenizer Instance { get; } = new();

    public IEnumerable<Token> Tokenize(string text)
    {
        if (!string.IsNullOrEmpty(text))
        {
            yield return new Token(text, 0, 0, text.Length);
        }
    }
}
