using Chroma.Core.Sdl.Lexing;
using Chroma.Core.Sdl.Source;

namespace Chroma.Core.Sdl.Syntax;

/// <summary>
/// Recursive-descent parser for the grammar in documents/scene-language.md.
/// </summary>
/// <remarks>
/// Four decisions in here are load-bearing and easy to get wrong later:
/// <list type="bullet">
/// <item>an identifier followed by <c>{</c> is a node, an identifier alone is a reference
/// to a <c>let</c> binding — one token of lookahead;</item>
/// <item>inside a block, an identifier followed by <c>:</c> is a field and anything else
/// starts a child — two tokens of lookahead;</item>
/// <item>commas are optional separators, consumed and discarded wherever they appear;</item>
/// <item>after <c>if (...)</c>, a <c>{</c> opens a body at statement level and an object
/// literal at expression level. Position settles it, so neither reading needs lookahead
/// and neither is ever ambiguous.</item>
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
        return new Token(kind, new SourceSpan(Current.Span.Start, 0, Current.Span.Source), string.Empty);
    }

    private SceneFile ParseSceneFile()
    {
        IReadOnlyList<Statement> statements = ParseStatements(TokenKind.EndOfFile, null);

        // From the end-of-file token, so the span names the file actually parsed rather than
        // the one the diagnostic bag was opened on. They differ for an included fragment.
        SourceSpan eof = _tokens[^1].Span;
        return new SceneFile(new SourceSpan(0, eof.End, eof.Source), statements);
    }

    /// <summary>
    /// Statements up to <paramref name="terminator"/>, which is not consumed.
    /// </summary>
    /// <remarks>
    /// One routine for the file, for a block and for a control-flow body. The terminator is
    /// the only thing that differs, which is the point of having made entries and statements
    /// the same thing.
    /// </remarks>
    private List<Statement> ParseStatements(TokenKind terminator, string? context)
    {
        List<Statement> statements = [];

        while (Current.Kind != terminator && Current.Kind != TokenKind.EndOfFile)
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
                string where = context is null ? string.Empty : $" in {context}";
                _diagnostics.Error(Current.Span, $"unexpected {Current.Describe()}{where}");
                Advance();
            }
        }

        return statements;
    }

    private Statement? ParseStatement()
    {
        switch (Current.Kind)
        {
            case TokenKind.Let:
                return ParseLetStatement();

            case TokenKind.If:
                return ParseIfStatement();

            case TokenKind.For:
                return ParseForStatement();

            case TokenKind.Include:
                return ParseIncludeStatement();

            // Stray separators between items are harmless; swallow them so a file that ends
            // a block with a comma does not produce a cascade of errors.
            case TokenKind.Comma:
            case TokenKind.Semicolon:
                Advance();
                return null;
        }

        // 'name:' is a field; anything else starts a child expression. A field at the top
        // level parses fine and is rejected by the binder, which can say what is wrong with
        // it rather than complaining about the colon.
        if (Current.Kind == TokenKind.Identifier && Peek(1).Kind == TokenKind.Colon)
        {
            Token name = Advance();
            Advance();

            Expression fieldValue = ParseExpression();
            return new FieldStatement(
                SourceSpan.Union(name.Span, fieldValue.Span), name.Text, name.Span, fieldValue);
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

    private Statement ParseIfStatement()
    {
        Token keyword = Advance();

        Expect(TokenKind.LeftParen, "'(' after 'if'");
        Expression condition = ParseExpression();
        Expect(TokenKind.RightParen, "')' after the condition");

        IReadOnlyList<Statement> then = ParseBody();
        List<Statement> otherwise = [];
        SourceSpan end = then.Count == 0 ? condition.Span : then[^1].Span;

        if (Current.Kind == TokenKind.Else)
        {
            Advance();

            // 'else if' chains without a brace, the way every language with this syntax
            // does it: the nested 'if' is the whole of the else body.
            otherwise = Current.Kind == TokenKind.If
                ? [ParseIfStatement()]
                : [.. ParseBody()];

            if (otherwise.Count > 0)
            {
                end = otherwise[^1].Span;
            }
        }

        return new IfStatement(SourceSpan.Union(keyword.Span, end), condition, then, otherwise);
    }

    private Statement ParseForStatement()
    {
        Token keyword = Advance();

        Expect(TokenKind.LeftParen, "'(' after 'for'");
        Token variable = Expect(TokenKind.Identifier, "a loop variable name");
        Expect(TokenKind.In, "'in'");

        Expression from = ParseExpression();
        Expect(TokenKind.DotDot, "'..' between the bounds of the range");
        Expression to = ParseExpression();
        Expect(TokenKind.RightParen, "')' after the range");

        IReadOnlyList<Statement> body = ParseBody();
        SourceSpan end = body.Count == 0 ? to.Span : body[^1].Span;

        return new ForStatement(
            SourceSpan.Union(keyword.Span, end),
            keyword.Span,
            variable.Text,
            variable.Span,
            from,
            to,
            body);
    }

    private Statement ParseIncludeStatement()
    {
        Token keyword = Advance();
        Token path = Expect(TokenKind.String, "a quoted file name after 'include'");
        Expect(TokenKind.Semicolon, "';' at the end of an 'include'");

        return new IncludeStatement(
            SourceSpan.Union(keyword.Span, path.Span), path.Text, path.Span);
    }

    /// <summary>
    /// The body of an <c>if</c> or a <c>for</c>: a braced group, or a single statement.
    /// </summary>
    /// <remarks>
    /// A <c>{</c> here is always a body and never an object literal. That is what makes
    /// <c>if (corner) { material: gold }</c> mean "add this field when corner", which is the
    /// reading the language wants, and it costs nothing: an anonymous object literal has no
    /// type name, so one written as a statement could not have been bound anyway.
    /// </remarks>
    private IReadOnlyList<Statement> ParseBody()
    {
        if (Current.Kind != TokenKind.LeftBrace)
        {
            Statement? single = ParseStatement();
            return single is null ? [] : [single];
        }

        Advance();
        List<Statement> body = ParseStatements(TokenKind.RightBrace, "the body");
        Expect(TokenKind.RightBrace, "'}'");
        return body;
    }

    private Expression ParseExpression() => ParseOr();

    private Expression ParseOr()
    {
        Expression left = ParseAnd();

        while (Current.Kind == TokenKind.PipePipe)
        {
            Advance();
            Expression right = ParseAnd();
            left = new BinaryExpression(
                SourceSpan.Union(left.Span, right.Span), BinaryOperator.Or, left, right);
        }

        return left;
    }

    private Expression ParseAnd()
    {
        Expression left = ParseEquality();

        while (Current.Kind == TokenKind.AmpersandAmpersand)
        {
            Advance();
            Expression right = ParseEquality();
            left = new BinaryExpression(
                SourceSpan.Union(left.Span, right.Span), BinaryOperator.And, left, right);
        }

        return left;
    }

    private Expression ParseEquality()
    {
        Expression left = ParseComparison();

        while (Current.Kind is TokenKind.EqualsEquals or TokenKind.BangEquals)
        {
            BinaryOperator op = Advance().Kind == TokenKind.EqualsEquals
                ? BinaryOperator.Equal
                : BinaryOperator.NotEqual;

            Expression right = ParseComparison();
            left = new BinaryExpression(SourceSpan.Union(left.Span, right.Span), op, left, right);
        }

        return left;
    }

    private Expression ParseComparison()
    {
        Expression left = ParseAdditive();

        while (Current.Kind is TokenKind.Less or TokenKind.LessEquals
            or TokenKind.Greater or TokenKind.GreaterEquals)
        {
            BinaryOperator op = Advance().Kind switch
            {
                TokenKind.Less => BinaryOperator.Less,
                TokenKind.LessEquals => BinaryOperator.LessOrEqual,
                TokenKind.Greater => BinaryOperator.Greater,
                _ => BinaryOperator.GreaterOrEqual,
            };

            Expression right = ParseAdditive();
            left = new BinaryExpression(SourceSpan.Union(left.Span, right.Span), op, left, right);
        }

        return left;
    }

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
        if (Current.Kind is not (TokenKind.Minus or TokenKind.Bang))
        {
            return ParsePrimary();
        }

        Token op = Advance();
        Expression operand = ParseUnary();
        SourceSpan span = SourceSpan.Union(op.Span, operand.Span);

        UnaryOperator kind = op.Kind == TokenKind.Minus
            ? UnaryOperator.Negate
            : UnaryOperator.Not;

        return new UnaryExpression(span, kind, operand);
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

            case TokenKind.True:
            case TokenKind.False:
            {
                Token token = Advance();
                return new BooleanExpression(token.Span, token.Kind == TokenKind.True);
            }

            case TokenKind.LeftBracket:
                return ParseVector();

            case TokenKind.If:
                return ParseConditionalExpression();

            case TokenKind.LeftBrace:
            {
                (SourceSpan span, IReadOnlyList<Statement> body) = ParseBlock();
                return new ObjectExpression(span, null, default, body);
            }

            case TokenKind.Identifier:
            {
                Token name = Advance();

                if (Current.Kind != TokenKind.LeftBrace)
                {
                    return new IdentifierExpression(name.Span, name.Text);
                }

                (SourceSpan blockSpan, IReadOnlyList<Statement> body) = ParseBlock();
                SourceSpan span = SourceSpan.Union(name.Span, blockSpan);
                return new ObjectExpression(span, name.Text, name.Span, body);
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
                return new MissingExpression(new SourceSpan(Current.Span.Start, 0, Current.Span.Source));
            }
        }
    }

    /// <summary>
    /// <c>if (cond) a else b</c> in a position where a value is wanted.
    /// </summary>
    /// <remarks>
    /// The <c>else</c> is not optional, and the message says why rather than naming the
    /// missing token: someone who meant the statement form and left the braces off gets
    /// told which one they wrote.
    /// </remarks>
    private Expression ParseConditionalExpression()
    {
        Token keyword = Advance();

        Expect(TokenKind.LeftParen, "'(' after 'if'");
        Expression condition = ParseExpression();
        Expect(TokenKind.RightParen, "')' after the condition");

        Expression whenTrue = ParseExpression();

        if (Current.Kind != TokenKind.Else)
        {
            _diagnostics.Error(
                SourceSpan.Union(keyword.Span, whenTrue.Span),
                "an 'if' used as a value needs an 'else'; "
                + "write 'if (...) { ... }' to make an entry conditional instead");

            return new MissingExpression(new SourceSpan(keyword.Span.Start, 0, keyword.Span.Source));
        }

        Advance();
        Expression whenFalse = ParseExpression();

        return new ConditionalExpression(
            SourceSpan.Union(keyword.Span, whenFalse.Span), condition, whenTrue, whenFalse);
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

    private (SourceSpan Span, IReadOnlyList<Statement> Body) ParseBlock()
    {
        Token open = Advance();
        List<Statement> body = ParseStatements(TokenKind.RightBrace, "a block");
        Token close = Expect(TokenKind.RightBrace, "'}'");

        return (SourceSpan.Union(open.Span, close.Span), body);
    }
}
