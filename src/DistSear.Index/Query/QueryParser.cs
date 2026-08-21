using System.Globalization;
using DistSear.Abstractions.Mapping;
using DistSear.Analysis;
using DistSear.Index.Segments;

namespace DistSear.Index.Query;

/// <summary>
/// Recursive-descent parser for the query syntax:
/// <code>
///   title:(fast AND search) -tag:draft "exact phrase"~2 price:[10 TO 50] serch~1 boosted^3
/// </code>
/// Precedence, loosest to tightest: OR, AND, NOT, then a single clause. Bare terms combine with the
/// default operator.
/// </summary>
public sealed class QueryParser
{
    private readonly IndexMapping _mapping;
    private readonly AnalyzerRegistry _analyzers;
    private readonly Occur _defaultOccur;

    private List<QueryToken> _tokens = [];
    private int _index;
    private string _input = string.Empty;

    public QueryParser(
        IndexMapping mapping,
        AnalyzerRegistry analyzers,
        Occur defaultOccur = Occur.Should)
    {
        _mapping = mapping;
        _analyzers = analyzers;
        _defaultOccur = defaultOccur;
    }

    /// <summary>Maximum edit distance a <c>~</c> without an explicit number implies.</summary>
    public int DefaultFuzziness { get; init; } = 2;

    public Query Parse(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return MatchAllQuery.Instance;
        }

        _input = query;
        _tokens = new QueryLexer(query).Tokenize();
        _index = 0;

        var parsed = ParseOr(_mapping.DefaultField);

        if (Current.Kind != TokenKind.End)
        {
            throw new QueryParseException($"Unexpected '{Current.Text}'", Current.Position, _input);
        }

        return parsed;
    }

    private QueryToken Current => _tokens[_index];

    private QueryToken Peek(int offset = 1) =>
        _index + offset < _tokens.Count ? _tokens[_index + offset] : _tokens[^1];

    private QueryToken Consume() => _tokens[_index++];

    private Query ParseOr(string field)
    {
        var clauses = new List<BooleanClause> { new(ParseAnd(field), Occur.Should) };
        var sawExplicitOr = false;

        while (Current.Kind == TokenKind.Or)
        {
            Consume();
            sawExplicitOr = true;
            clauses.Add(new BooleanClause(ParseAnd(field), Occur.Should));
        }

        if (clauses.Count == 1 && !sawExplicitOr)
        {
            return clauses[0].Query;
        }

        return new BooleanQuery(clauses);
    }

    private Query ParseAnd(string field)
    {
        var first = ParseClause(field, out var firstOccur);
        var clauses = new List<BooleanClause>();

        if (first is not null)
        {
            clauses.Add(new BooleanClause(first, firstOccur));
        }

        var explicitAnd = false;

        while (true)
        {
            if (Current.Kind == TokenKind.And)
            {
                Consume();
                explicitAnd = true;

                var next = ParseClause(field, out var occur);

                if (next is not null)
                {
                    // An explicit AND upgrades both sides to required.
                    clauses.Add(new BooleanClause(next, occur == Occur.Should ? Occur.Must : occur));
                }

                continue;
            }

            if (Current.Kind is TokenKind.End or TokenKind.RightParen or TokenKind.Or)
            {
                break;
            }

            var adjacent = ParseClause(field, out var adjacentOccur);

            if (adjacent is null)
            {
                break;
            }

            clauses.Add(new BooleanClause(adjacent, adjacentOccur));
        }

        if (explicitAnd)
        {
            for (var i = 0; i < clauses.Count; i++)
            {
                if (clauses[i].Occur == Occur.Should)
                {
                    clauses[i] = clauses[i] with { Occur = Occur.Must };
                }
            }
        }

        if (clauses.Count == 0)
        {
            throw new QueryParseException("Empty query", Current.Position, _input);
        }

        if (clauses.Count == 1 && clauses[0].Occur is Occur.Should or Occur.Must)
        {
            return clauses[0].Query;
        }

        return new BooleanQuery(clauses);
    }

    private Query? ParseClause(string field, out Occur occur)
    {
        occur = _defaultOccur;

        switch (Current.Kind)
        {
            case TokenKind.Plus:
                Consume();
                occur = Occur.Must;
                break;

            case TokenKind.Minus:
            case TokenKind.Not:
                Consume();
                occur = Occur.MustNot;
                break;
        }

        // A field prefix rebinds the target for this clause only.
        if (Current.Kind == TokenKind.Word && Peek().Kind == TokenKind.Colon)
        {
            var name = Consume().Text;
            Consume();

            if (!_mapping.Has(name))
            {
                throw new QueryParseException($"Unknown field '{name}'", Current.Position, _input);
            }

            field = name;
        }

        var query = ParsePrimary(field);

        if (query is null)
        {
            return null;
        }

        if (Current.Kind == TokenKind.Caret)
        {
            Consume();
            query = new BoostQuery(query, ReadNumber());
        }

        return query;
    }

    private Query? ParsePrimary(string field)
    {
        switch (Current.Kind)
        {
            case TokenKind.LeftParen:
            {
                var open = Consume();
                var inner = ParseOr(field);

                if (Current.Kind != TokenKind.RightParen)
                {
                    throw new QueryParseException("Unclosed group", open.Position, _input);
                }

                Consume();
                return inner;
            }

            case TokenKind.Phrase:
                return ParsePhrase(field);

            case TokenKind.RangeStart:
                return ParseRange(field);

            case TokenKind.Word:
                return ParseTerm(field);

            case TokenKind.End:
            case TokenKind.RightParen:
                return null;

            default:
                throw new QueryParseException($"Unexpected '{Current.Text}'", Current.Position, _input);
        }
    }

    private Query ParsePhrase(string field)
    {
        var text = Consume().Text;
        var slop = 0;

        if (Current.Kind == TokenKind.Tilde)
        {
            Consume();
            slop = (int)ReadNumber();
        }

        var mapping = _mapping.Require(field);
        var terms = _analyzers.ForSearching(mapping).AnalyzeToTerms(text);

        if (terms.Count == 0)
        {
            // Every token was a stopword: nothing can be required, so match nothing rather than
            // silently matching everything.
            return new TermsQuery(field, []);
        }

        return terms.Count == 1
            ? new TermQuery(field, terms[0])
            : new PhraseQuery(field, terms, slop);
    }

    private Query ParseRange(string field)
    {
        var open = Consume();

        var lower = ReadRangeBound();

        if (Current.Kind != TokenKind.To)
        {
            throw new QueryParseException("Expected TO in range", Current.Position, _input);
        }

        Consume();
        var upper = ReadRangeBound();

        if (Current.Kind != TokenKind.RangeEnd)
        {
            throw new QueryParseException("Unclosed range", open.Position, _input);
        }

        var close = Consume();
        var mapping = _mapping.Require(field);

        if (FieldCoercion.IsNumeric(mapping.Type))
        {
            return new NumericRangeQuery(
                field,
                lower is null ? null : FieldCoercion.ToSortableDouble(lower, mapping.Type),
                upper is null ? null : FieldCoercion.ToSortableDouble(upper, mapping.Type),
                open.Inclusive,
                close.Inclusive);
        }

        return new TermRangeQuery(field, lower, upper, open.Inclusive, close.Inclusive);
    }

    /// <summary>Reads one end of a range. <c>*</c> means unbounded on that side.</summary>
    private string? ReadRangeBound()
    {
        if (Current.Kind == TokenKind.Phrase)
        {
            return Consume().Text;
        }

        if (Current.Kind != TokenKind.Word)
        {
            throw new QueryParseException("Expected a range bound", Current.Position, _input);
        }

        var text = Consume().Text;
        return text == "*" ? null : text;
    }

    private Query ParseTerm(string field)
    {
        var token = Consume();
        var text = token.Text;
        var mapping = _mapping.Require(field);

        if (text == "*")
        {
            return MatchAllQuery.Instance;
        }

        if (Current.Kind == TokenKind.Tilde)
        {
            Consume();

            var edits = Current.Kind == TokenKind.Word && IsNumber(Current.Text)
                ? (int)ReadNumber()
                : DefaultFuzziness;

            // Fuzzy terms bypass stemming: the point is to match what the user typed, and stemming
            // first would measure the distance between stems instead of between words.
            return new FuzzyQuery(field, Normalise(text, mapping), Math.Clamp(edits, 0, 2));
        }

        if (text.Contains('*') || text.Contains('?'))
        {
            // Wildcards likewise skip analysis, since a pattern is not a word.
            return text.EndsWith('*') && text.IndexOfAny(['*', '?']) == text.Length - 1
                ? new PrefixQuery(field, Normalise(text[..^1], mapping))
                : new WildcardQuery(field, Normalise(text, mapping));
        }

        if (mapping.Type != FieldType.Text)
        {
            return new TermQuery(field, FieldCoercion.ToTerm(text, mapping.Type));
        }

        var terms = _analyzers.ForSearching(mapping).AnalyzeToTerms(text);

        return terms.Count switch
        {
            0 => new TermsQuery(field, []),
            1 => new TermQuery(field, terms[0]),

            // One input word expanding to several terms (a synonym rule, say) is a disjunction:
            // any of the expansions matching counts as the word matching.
            _ => new BooleanQuery([.. terms.Select(t => new BooleanClause(new TermQuery(field, t), Occur.Should))])
        };
    }

    /// <summary>
    /// Case-folds a pattern that skips the analyzer, so it still lines up with indexed terms that
    /// went through lowercasing.
    /// </summary>
    private static string Normalise(string text, FieldMapping mapping) =>
        mapping.Type == FieldType.Keyword ? text : text.ToLowerInvariant();

    private static bool IsNumber(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _);

    private double ReadNumber()
    {
        if (Current.Kind != TokenKind.Word || !IsNumber(Current.Text))
        {
            throw new QueryParseException("Expected a number", Current.Position, _input);
        }

        return double.Parse(Consume().Text, CultureInfo.InvariantCulture);
    }
}
