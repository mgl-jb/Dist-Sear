using System.Text;

namespace DistSear.Index.Query;

internal enum TokenKind
{
    Word,
    Phrase,
    LeftParen,
    RightParen,
    Colon,
    Plus,
    Minus,
    Caret,
    Tilde,
    RangeStart,
    RangeEnd,
    And,
    Or,
    Not,
    To,
    End
}

internal readonly record struct QueryToken(TokenKind Kind, string Text, int Position, bool Inclusive = false);

/// <summary>
/// Turns a query string into tokens.
///
/// The one context-sensitive rule is <c>+</c> and <c>-</c>: they only mean "required" and
/// "prohibited" at the start of a clause. Anywhere else they are ordinary word characters, so
/// <c>e-mail</c> and <c>covid-19</c> lex as single terms rather than as a subtraction.
/// </summary>
internal sealed class QueryLexer
{
    private readonly string _input;
    private int _position;

    public QueryLexer(string input) => _input = input;

    public List<QueryToken> Tokenize()
    {
        var tokens = new List<QueryToken>();

        while (true)
        {
            var token = Next(tokens.Count == 0 ? null : tokens[^1].Kind);
            tokens.Add(token);

            if (token.Kind == TokenKind.End)
            {
                return tokens;
            }
        }
    }

    private QueryToken Next(TokenKind? previous)
    {
        SkipWhitespace();

        if (_position >= _input.Length)
        {
            return new QueryToken(TokenKind.End, string.Empty, _position);
        }

        var start = _position;
        var c = _input[_position];

        switch (c)
        {
            case '(':
                _position++;
                return new QueryToken(TokenKind.LeftParen, "(", start);
            case ')':
                _position++;
                return new QueryToken(TokenKind.RightParen, ")", start);
            case ':':
                _position++;
                return new QueryToken(TokenKind.Colon, ":", start);
            case '^':
                _position++;
                return new QueryToken(TokenKind.Caret, "^", start);
            case '~':
                _position++;
                return new QueryToken(TokenKind.Tilde, "~", start);
            case '[':
                _position++;
                return new QueryToken(TokenKind.RangeStart, "[", start, Inclusive: true);
            case '{':
                _position++;
                return new QueryToken(TokenKind.RangeStart, "{", start, Inclusive: false);
            case ']':
                _position++;
                return new QueryToken(TokenKind.RangeEnd, "]", start, Inclusive: true);
            case '}':
                _position++;
                return new QueryToken(TokenKind.RangeEnd, "}", start, Inclusive: false);
            case '"':
                return ReadPhrase();
        }

        if ((c == '+' || c == '-') && StartsAClause(previous))
        {
            _position++;
            return new QueryToken(c == '+' ? TokenKind.Plus : TokenKind.Minus, c.ToString(), start);
        }

        return ReadWord();
    }

    /// <summary>
    /// True when the previous token leaves us expecting a fresh clause, which is the only place a
    /// leading sign can be an operator.
    /// </summary>
    private static bool StartsAClause(TokenKind? previous) => previous switch
    {
        null => true,
        TokenKind.LeftParen => true,
        TokenKind.And or TokenKind.Or or TokenKind.Not => true,
        TokenKind.Plus or TokenKind.Minus => true,
        TokenKind.Colon => false,
        _ => true
    };

    private void SkipWhitespace()
    {
        while (_position < _input.Length && char.IsWhiteSpace(_input[_position]))
        {
            _position++;
        }
    }

    private QueryToken ReadPhrase()
    {
        var start = _position;
        _position++;

        var builder = new StringBuilder();

        while (_position < _input.Length && _input[_position] != '"')
        {
            if (_input[_position] == '\\' && _position + 1 < _input.Length)
            {
                _position++;
            }

            builder.Append(_input[_position++]);
        }

        if (_position >= _input.Length)
        {
            throw new QueryParseException("Unterminated phrase", start, _input);
        }

        _position++;
        return new QueryToken(TokenKind.Phrase, builder.ToString(), start);
    }

    private QueryToken ReadWord()
    {
        var start = _position;
        var builder = new StringBuilder();

        while (_position < _input.Length)
        {
            var c = _input[_position];

            if (char.IsWhiteSpace(c) || "():^~[]{}\"".Contains(c))
            {
                break;
            }

            if (c == '\\' && _position + 1 < _input.Length)
            {
                _position++;
                builder.Append(_input[_position++]);
                continue;
            }

            builder.Append(c);
            _position++;
        }

        var text = builder.ToString();

        var kind = text switch
        {
            "AND" or "&&" => TokenKind.And,
            "OR" or "||" => TokenKind.Or,
            "NOT" or "!" => TokenKind.Not,
            "TO" => TokenKind.To,
            _ => TokenKind.Word
        };

        return new QueryToken(kind, text, start);
    }
}
