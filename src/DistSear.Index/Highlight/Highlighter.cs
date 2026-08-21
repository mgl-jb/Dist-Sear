using System.Text;
using DistSear.Abstractions.Mapping;
using DistSear.Abstractions.Search;
using DistSear.Analysis;

namespace DistSear.Index.Highlight;

/// <summary>
/// Produces marked-up snippets showing why a document matched.
///
/// The stored text is re-analyzed so that the terms compared are exactly the ones the index holds —
/// stemming and folding included, so a query for "searching" highlights the word "searches" as it
/// appears in the original text. Token offsets map each match back to the raw string, which is what
/// keeps the returned snippet faithful to what the author wrote rather than to its analyzed form.
/// </summary>
public sealed class Highlighter
{
    private readonly AnalyzerRegistry _analyzers;

    public Highlighter(AnalyzerRegistry analyzers) => _analyzers = analyzers;

    public IReadOnlyList<string> Highlight(
        FieldMapping field,
        string text,
        IReadOnlyList<IHighlightMatcher> matchers,
        HighlightSpec spec)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var relevant = matchers
            .Where(m => string.Equals(m.Field, field.Name, StringComparison.Ordinal))
            .ToList();

        if (relevant.Count == 0)
        {
            return [];
        }

        var matches = FindMatches(field, text, relevant);

        if (matches.Count == 0)
        {
            return [];
        }

        var passages = BuildPassages(text, matches, spec);

        return [.. passages
            .OrderByDescending(p => p.Score)
            .ThenBy(p => p.Start)
            .Take(spec.MaxFragments)
            .Select(p => Render(text, p, spec))];
    }

    private List<(int Start, int End)> FindMatches(
        FieldMapping field,
        string text,
        List<IHighlightMatcher> matchers)
    {
        var analyzer = _analyzers.ForIndexing(field);
        var matches = new List<(int Start, int End)>();

        foreach (var token in analyzer.Analyze(text))
        {
            foreach (var matcher in matchers)
            {
                if (matcher.Matches(token.Term))
                {
                    matches.Add((token.StartOffset, token.EndOffset));
                    break;
                }
            }
        }

        return matches;
    }

    private sealed record Passage(int Start, int End, List<(int Start, int End)> Matches)
    {
        public int Score => Matches.Count;
    }

    /// <summary>
    /// Groups matches into windows of roughly the requested size. Matches close together share a
    /// passage rather than each producing a near-duplicate snippet.
    /// </summary>
    private static List<Passage> BuildPassages(
        string text,
        List<(int Start, int End)> matches,
        HighlightSpec spec)
    {
        var passages = new List<Passage>();
        var index = 0;

        while (index < matches.Count)
        {
            var first = matches[index];

            // Centre the window on the first unclaimed match.
            var start = Math.Max(0, first.Start - (spec.FragmentSize / 2));
            var end = Math.Min(text.Length, start + spec.FragmentSize);

            var claimed = new List<(int Start, int End)>();

            while (index < matches.Count && matches[index].End <= end)
            {
                claimed.Add(matches[index]);
                index++;
            }

            // A match longer than the window still has to be claimed, or the loop cannot advance.
            if (claimed.Count == 0)
            {
                claimed.Add(first);
                end = Math.Min(text.Length, first.End);
                index++;
            }

            passages.Add(new Passage(SnapToWordStart(text, start), SnapToWordEnd(text, end), claimed));
        }

        return passages;
    }

    /// <summary>Nudges a cut point off the middle of a word, so snippets do not start mid-token.</summary>
    private static int SnapToWordStart(string text, int position)
    {
        if (position <= 0)
        {
            return 0;
        }

        while (position < text.Length && !char.IsWhiteSpace(text[position - 1]))
        {
            position++;
        }

        return position;
    }

    private static int SnapToWordEnd(string text, int position)
    {
        if (position >= text.Length)
        {
            return text.Length;
        }

        while (position > 0 && position < text.Length && !char.IsWhiteSpace(text[position]))
        {
            position--;
        }

        return position > 0 ? position : text.Length;
    }

    private static string Render(string text, Passage passage, HighlightSpec spec)
    {
        var builder = new StringBuilder();
        var cursor = passage.Start;

        foreach (var (start, end) in passage.Matches)
        {
            if (start < cursor || end > passage.End)
            {
                continue;
            }

            builder.Append(text, cursor, start - cursor);
            builder.Append(spec.PreTag);
            builder.Append(text, start, end - start);
            builder.Append(spec.PostTag);
            cursor = end;
        }

        if (cursor < passage.End)
        {
            builder.Append(text, cursor, passage.End - cursor);
        }

        return builder.ToString().Trim();
    }
}
