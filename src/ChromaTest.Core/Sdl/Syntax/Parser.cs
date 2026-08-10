using ChromaTest.Core.Sdl.Lexing;
using ChromaTest.Core.Sdl.Source;

namespace ChromaTest.Core.Sdl.Syntax;

/// <summary>
/// Recursive-descent parser for the grammar in documents/scene-language.md.
/// </summary>
/// <remarks>
/// Three decisions in here are load-bearing and easy to get wrong later:
/// <list type="bullet">
/// <item>an identifier followed by <c>{</c> is a node, an identifier alone is a reference
/// to a <c>let</c> binding — one token of lookahead;</item>
/// <item>inside a block, an identifier followed by <c>:</c> is a field and anything else
/// starts a child — two tokens of lookahead;</item>
/// <item>commas are optional separators, consumed and discarded wherever they appear.</item>
/// </list>
/// The parser never throws. On an unexpected token it reports, emits a
/// <see cref="MissingExpression"/> and resynchronises, so a single run surfaces as many
/// problems as it can reach.
/// </remarks>
public sealed class Parser
{
    private readonly IReadOnlyList<Token> _tokens;
    private readonly DiagnosticBag _diagnostics;
    private int _index;

    private Parser(IReadOnlyList<Token> tokens, DiagnosticBag diagnostics)
    {
        _tokens = tokens;
        _diagnostics = diagnostics;
    }

    public static SceneFile Parse(IReadOnlyList<Token> tokens, DiagnosticBag diagnostics) =>
        new Parser(tokens, diagnostics).ParseSceneFile();

    private Token Current => Peek(0);

    private Token Peek(int offset)
    {
        int index = Math.Min(_index + offset, _tokens.Count - 1);
        return _tokens[index];
    }

    private Token Advance()
    {
        Token token = Current;

        if (_index < _tokens.Count - 1)
        {
            _index++;
        }

        return token;
    }

    private bool Match(TokenKind kind)
    {
        if (Current.Kind != kind)
        {
            return false;
        }

        Advance();
        return true;
    }

    private Token Expect(TokenKind kind, string what)
    {
        if (Current.Kind == kind)
        {
            return Advance();
        }

        _diagnostics.Error(Current.Span, $"expected {what}, found {Current.Describe()}");
        return new Token(kind, new SourceSpan(Current.Span.Start, 0), string.Empty);
    }

    private SceneFile ParseSceneFile()
    {
        List<Statement> statements = [];

        while (Current.Kind != TokenKind.EndOfFile)
        {
            int before = _index;

            Statement? statement = ParseStatement();
            if (statement is not null)
            {
                statements.Add(statement);
            }

            // Guarantee progress. Without this, any construct that fails to consume a
            // token turns the recovery path into an infinite loop.
            if (_index == before)
            {
                _diagnostics.Error(Current.Span, $"unexpected {Current.Describe()}");
                Advance();
            }
        }

        SourceSpan span = new(0, _diagnostics.Source.Length);
        return new SceneFile(span, statements);
    }

    private Statement? ParseStatement()
    {
        if (Current.Kind == TokenKind.Let)
        {
            return ParseLetStatement();
        }

        // Stray separators between top-level items are harmless; swallow them so a file
        // that ends a block with a comma does not produce a cascade of errors.
        if (Current.Kind is TokenKind.Comma or TokenKind.Semicolon)
        {
            Advance();
            return null;
        }

        Expression value = ParseExpression();
        return value is MissingExpression ? null : new ExpressionStatement(value.Span, value);
    }

    private Statement ParseLetStatement()
    {
        Token keyword = Advance();
        Token name = Expect(TokenKind.Identifier, "a name after 'let'");
        Expect(TokenKind.Equals, "'='");

        Expression value = ParseExpression();
        Expect(TokenKind.Semicolon, "';' at the end of a 'let'");

        SourceSpan span = SourceSpan.Union(keyword.Span, value.Span);
        return new LetStatement(span, name.Text, name.Span, value);
    }

    private Expression ParseExpression() => ParseAdditive();

    private Expression ParseAdditive()
    {
        Expression left = ParseMultiplicative();

        while (Current.Kind is TokenKind.Plus or TokenKind.Minus)
        {
            BinaryOperator op = Advance().Kind == TokenKind.Plus
                ? BinaryOperator.Add
                : BinaryOperator.Subtract;

            Expression right = ParseMultiplicative();
            left = new BinaryExpression(SourceSpan.Union(left.Span, right.Span), op, left, right);
        }

        return left;
    }

    private Expression ParseMultiplicative()
    {
        Expression left = ParseUnary();

        while (Current.Kind is TokenKind.Star or TokenKind.Slash)
        {
            BinaryOperator op = Advance().Kind == TokenKind.Star
                ? BinaryOperator.Multiply
                : BinaryOperator.Divide;

            Expression right = ParseUnary();
            left = new BinaryExpression(SourceSpan.Union(left.Span, right.Span), op, left, right);
        }

        return left;
    }

    private Expression ParseUnary()
    {
        if (Current.Kind != TokenKind.Minus)
        {
            return ParsePrimary();
        }

        Token op = Advance();
        Expression operand = ParseUnary();
        SourceSpan span = SourceSpan.Union(op.Span, operand.Span);
        return new UnaryExpression(span, UnaryOperator.Negate, operand);
    }

    private Expression ParsePrimary()
    {
        switch (Current.Kind)
        {
            case TokenKind.Number:
            {
                Token token = Advance();
                return new NumberExpression(token.Span, token.NumberValue);
            }

            case TokenKind.String:
            {
                Token token = Advance();
                return new StringExpression(token.Span, token.Text);
            }

            case TokenKind.LeftBracket:
                return ParseVector();

            case TokenKind.LeftBrace:
            {
                (SourceSpan span, IReadOnlyList<BlockEntry> entries) = ParseBlock();
                return new ObjectExpression(span, null, default, entries);
            }

            case TokenKind.Identifier:
            {
                Token name = Advance();

                if (Current.Kind != TokenKind.LeftBrace)
                {
                    return new IdentifierExpression(name.Span, name.Text);
                }

                (SourceSpan blockSpan, IReadOnlyList<BlockEntry> entries) = ParseBlock();
                SourceSpan span = SourceSpan.Union(name.Span, blockSpan);
                return new ObjectExpression(span, name.Text, name.Span, entries);
            }

            case TokenKind.LeftParen:
            {
                Advance();
                Expression inner = ParseExpression();
                Expect(TokenKind.RightParen, "')'");
                return inner;
            }

            default:
            {
                // Do not advance: the caller's progress guard decides whether to skip this
                // token, which keeps recovery decisions in one place.
                _diagnostics.Error(Current.Span, $"expected a value, found {Current.Describe()}");
                return new MissingExpression(new SourceSpan(Current.Span.Start, 0));
            }
        }
    }

    private Expression ParseVector()
    {
        Token open = Advance();
        List<Expression> components = [];

        while (Current.Kind is not (TokenKind.RightBracket or TokenKind.EndOfFile))
        {
            int before = _index;

            if (Match(TokenKind.Comma))
            {
                continue;
            }

            components.Add(ParseExpression());

            if (_index == before)
            {
                Advance();
            }
        }

        Token close = Expect(TokenKind.RightBracket, "']'");
        return new VectorExpression(SourceSpan.Union(open.Span, close.Span), components);
    }

    private (SourceSpan Span, IReadOnlyList<BlockEntry> Entries) ParseBlock()
    {
        Token open = Advance();
        List<BlockEntry> entries = [];

        while (Current.Kind is not (TokenKind.RightBrace or TokenKind.EndOfFile))
        {
            int before = _index;

            if (Match(TokenKind.Comma))
            {
                continue;
            }

            BlockEntry? entry = ParseBlockEntry();
            if (entry is not null)
            {
                entries.Add(entry);
            }

            if (_index == before)
            {
                _diagnostics.Error(Current.Span, $"unexpected {Current.Describe()} in a block");
                Advance();
            }
        }

        Token close = Expect(TokenKind.RightBrace, "'}'");
        return (SourceSpan.Union(open.Span, close.Span), entries);
    }

    private BlockEntry? ParseBlockEntry()
    {
        // 'name:' is a field; anything else starts a child expression.
        if (Current.Kind == TokenKind.Identifier && Peek(1).Kind == TokenKind.Colon)
        {
            Token name = Advance();
            Advance();

            Expression value = ParseExpression();
            SourceSpan span = SourceSpan.Union(name.Span, value.Span);
            return new FieldEntry(span, name.Text, name.Span, value);
        }

        Expression child = ParseExpression();
        return child is MissingExpression ? null : new ChildEntry(child.Span, child);
    }
}
