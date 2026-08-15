using System.Globalization;
using Chroma.Core.Sdl.Source;

namespace Chroma.Core.Sdl.Lexing;

/// <summary>
/// Turns scene file text into a flat token list. Never throws: an unrecognised character
/// becomes a <see cref="TokenKind.Bad"/> token and a diagnostic, and lexing continues, so
/// one typo does not hide every later problem in the file.
/// </summary>
public sealed class Lexer
{
    private readonly SourceText _source;
    private readonly DiagnosticBag _diagnostics;
    private int _position;

    private Lexer(SourceText source, DiagnosticBag diagnostics)
    {
        _source = source;
        _diagnostics = diagnostics;
    }

    public static IReadOnlyList<Token> Tokenize(SourceText source, DiagnosticBag diagnostics)
    {
        Lexer lexer = new(source, diagnostics);
        List<Token> tokens = [];

        while (true)
        {
            Token token = lexer.NextToken();
            tokens.Add(token);

            if (token.Kind == TokenKind.EndOfFile)
            {
                return tokens;
            }
        }
    }

    private char Current => Peek(0);

    private char Peek(int offset)
    {
        int index = _position + offset;
        return index < _source.Length ? _source[index] : '\0';
    }

    private Token NextToken()
    {
        SkipTrivia();

        int start = _position;

        if (_position >= _source.Length)
        {
            return new Token(TokenKind.EndOfFile, Span(start, 0), string.Empty);
        }

        char c = Current;

        if (char.IsAsciiDigit(c))
        {
            return ReadNumber();
        }

        if (char.IsAsciiLetter(c) || c == '_')
        {
            return ReadIdentifier();
        }

        if (c == '"')
        {
            return ReadString();
        }

        // Two-character operators first: every one of them starts with a character that is
        // also a token on its own, so testing the pair before the single is what stops
        // '<=' lexing as '<' followed by an unexpected '='.
        char next = Peek(1);

        TokenKind? pair = (c, next) switch
        {
            ('=', '=') => TokenKind.EqualsEquals,
            ('!', '=') => TokenKind.BangEquals,
            ('<', '=') => TokenKind.LessEquals,
            ('>', '=') => TokenKind.GreaterEquals,
            ('&', '&') => TokenKind.AmpersandAmpersand,
            ('|', '|') => TokenKind.PipePipe,
            ('<', '<') => TokenKind.LessLess,
            ('>', '>') => TokenKind.GreaterGreater,
            ('+', '+') => TokenKind.PlusPlus,
            ('-', '-') => TokenKind.MinusMinus,
            ('.', '.') => TokenKind.DotDot,
            _ => null,
        };

        if (pair is not null)
        {
            _position += 2;
            SourceSpan pairSpan = Span(start, 2);
            return new Token(pair.Value, pairSpan, _source.GetText(pairSpan));
        }

        TokenKind kind = c switch
        {
            '{' => TokenKind.LeftBrace,
            '}' => TokenKind.RightBrace,
            '[' => TokenKind.LeftBracket,
            ']' => TokenKind.RightBracket,
            '(' => TokenKind.LeftParen,
            ')' => TokenKind.RightParen,
            ':' => TokenKind.Colon,
            ',' => TokenKind.Comma,
            ';' => TokenKind.Semicolon,
            '=' => TokenKind.Equals,
            '+' => TokenKind.Plus,
            '-' => TokenKind.Minus,
            '*' => TokenKind.Star,
            '/' => TokenKind.Slash,
            '%' => TokenKind.Percent,
            '?' => TokenKind.Question,
            '<' => TokenKind.Less,
            '>' => TokenKind.Greater,
            '!' => TokenKind.Bang,
            '&' => TokenKind.Ampersand,
            '|' => TokenKind.Pipe,
            '^' => TokenKind.Caret,
            '~' => TokenKind.Tilde,
            _ => TokenKind.Bad,
        };

        _position++;
        SourceSpan span = Span(start, 1);
        string text = _source.GetText(span);

        if (kind == TokenKind.Bad)
        {
            _diagnostics.Error(span, $"unexpected character '{text}'");
        }

        return new Token(kind, span, text);
    }

    /// <summary>
    /// A span in this file. Every span the lexer builds goes through here, so no token can
    /// end up unattributed once <c>include</c> puts several files in one load.
    /// </summary>
    private SourceSpan Span(int start, int length) => new(start, length, _source);

    /// <summary>Whitespace and both comment forms; block comments do not nest.</summary>
    private void SkipTrivia()
    {
        while (_position < _source.Length)
        {
            char c = Current;

            if (char.IsWhiteSpace(c))
            {
                _position++;
                continue;
            }

            if (c == '/' && Peek(1) == '/')
            {
                while (_position < _source.Length && Current != '\n')
                {
                    _position++;
                }

                continue;
            }

            if (c == '/' && Peek(1) == '*')
            {
                int start = _position;
                _position += 2;

                while (true)
                {
                    if (_position >= _source.Length)
                    {
                        _diagnostics.Error(
                            Span(start, 2),
                            "unterminated block comment, '*/' is missing");
                        return;
                    }

                    if (Current == '*' && Peek(1) == '/')
                    {
                        _position += 2;
                        break;
                    }

                    _position++;
                }

                continue;
            }

            return;
        }
    }

    private Token ReadNumber()
    {
        int start = _position;

        while (char.IsAsciiDigit(Current))
        {
            _position++;
        }

        if (Current == '.' && char.IsAsciiDigit(Peek(1)))
        {
            _position++;

            while (char.IsAsciiDigit(Current))
            {
                _position++;
            }
        }

        if ((Current == 'e' || Current == 'E')
            && (char.IsAsciiDigit(Peek(1))
                || ((Peek(1) == '+' || Peek(1) == '-') && char.IsAsciiDigit(Peek(2)))))
        {
            _position += 2;

            while (char.IsAsciiDigit(Current))
            {
                _position++;
            }
        }

        SourceSpan span = Span(start, _position - start);
        string text = _source.GetText(span);

        // InvariantCulture is not optional. The scene format writes 1.5 with a dot, and on
        // a machine whose current culture uses a decimal comma the default parse rejects
        // it -- so a perfectly valid file would fail to load on some machines and not
        // others. This is the single place the conversion happens.
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
        {
            _diagnostics.Error(span, $"'{text}' is not a valid number");
            return new Token(TokenKind.Bad, span, text);
        }

        return new Token(TokenKind.Number, span, text, value);
    }

    /// <summary>
    /// A double-quoted literal. No escape sequences, and no newline inside one.
    /// </summary>
    /// <remarks>
    /// Strings exist here to name a variant — <c>spline: "bezier"</c> — not to carry text, so
    /// there is nothing an escape would be needed for. Stopping at the newline is what keeps
    /// a missing closing quote a one-line mistake instead of swallowing the rest of the file
    /// and reporting the error somewhere unrelated.
    /// </remarks>
    private Token ReadString()
    {
        int start = _position;
        _position++;   // the opening quote

        while (_position < _source.Length && Current != '"' && Current != '\n')
        {
            _position++;
        }

        if (_position >= _source.Length || Current == '\n')
        {
            SourceSpan bad = Span(start, _position - start);
            _diagnostics.Error(bad, "unterminated string, '\"' is missing");
            return new Token(TokenKind.Bad, bad, _source.GetText(bad));
        }

        _position++;   // the closing quote

        SourceSpan span = Span(start, _position - start);
        string quoted = _source.GetText(span);

        // The token's Text is the contents, not the quotes: every consumer wants the value,
        // and Describe() below puts the quotes back for the one place that wants them.
        return new Token(TokenKind.String, span, quoted[1..^1]);
    }

    private Token ReadIdentifier()
    {
        int start = _position;

        while (char.IsAsciiLetterOrDigit(Current) || Current == '_')
        {
            _position++;
        }

        SourceSpan span = Span(start, _position - start);
        string text = _source.GetText(span);

        return new Token(Keyword(text), span, text);
    }

    /// <summary>
    /// The reserved word an identifier spells, or <see cref="TokenKind.Identifier"/>.
    /// </summary>
    /// <remarks>
    /// Reserving these is what could break a file written before they existed: a scene using
    /// <c>for</c> or <c>function</c> as the name of a binding or of a node stops parsing.
    /// None of the sample scenes does. <c>in</c> is reserved without appearing in the grammar
    /// at all — see <see cref="TokenKind.In"/> for why.
    /// </remarks>
    private static TokenKind Keyword(string text) => text switch
    {
        "let" => TokenKind.Let,
        "function" => TokenKind.Function,
        "return" => TokenKind.Return,
        "if" => TokenKind.If,
        "else" => TokenKind.Else,
        "for" => TokenKind.For,
        "in" => TokenKind.In,
        "true" => TokenKind.True,
        "false" => TokenKind.False,
        "include" => TokenKind.Include,
        _ => TokenKind.Identifier,
    };
}
