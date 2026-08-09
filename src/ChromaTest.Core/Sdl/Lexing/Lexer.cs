using System.Globalization;
using ChromaTest.Core.Sdl.Source;

namespace ChromaTest.Core.Sdl.Lexing;

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
            return new Token(TokenKind.EndOfFile, new SourceSpan(start, 0), string.Empty);
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
            _ => TokenKind.Bad,
        };

        _position++;
        SourceSpan span = new(start, 1);
        string text = _source.GetText(span);

        if (kind == TokenKind.Bad)
        {
            _diagnostics.Error(span, $"unexpected character '{text}'");
        }

        return new Token(kind, span, text);
    }

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
                            new SourceSpan(start, 2),
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

        SourceSpan span = new(start, _position - start);
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

    private Token ReadIdentifier()
    {
        int start = _position;

        while (char.IsAsciiLetterOrDigit(Current) || Current == '_')
        {
            _position++;
        }

        SourceSpan span = new(start, _position - start);
        string text = _source.GetText(span);
        TokenKind kind = text == "let" ? TokenKind.Let : TokenKind.Identifier;

        return new Token(kind, span, text);
    }
}
