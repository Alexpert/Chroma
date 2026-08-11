using Chroma.Core.Sdl.Lexing;
using Chroma.Core.Sdl.Source;

namespace Chroma.Core.Sdl.Syntax;

/// <summary>
/// Recursive-descent parser for the grammar in documents/scene-language.md.
/// </summary>
/// <remarks>
/// Four decisions in here are load-bearing and easy to get wrong later:
/// <list type="bullet">
/// <item>an identifier followed by <c>{</c> is a node, one followed by <c>(</c> is a call,
/// and one alone is a reference to a binding — one token of lookahead settles all three;</item>
/// <item>inside a block, an identifier followed by <c>:</c> is a field, one followed by
/// <c>=</c>, <c>++</c> or <c>--</c> is an assignment, and anything else starts a child — two
/// tokens of lookahead;</item>
/// <item>commas are optional separators, consumed and discarded wherever they appear;</item>
/// <item><c>{</c> after <c>if (…)</c>, <c>for (…)</c> or a parameter list is always a body
/// and never an object literal. That used to be settled by position against the <c>if</c>
/// expression; the ternary replaced it, and the ambiguity went with it.</item>
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

            case TokenKind.Function:
                return ParseFunctionStatement();

            case TokenKind.Return:
                return ParseReturnStatement();

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

        if (LooksLikeAnExpressionFunction())
        {
            return RejectExpressionFunction();
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

        if (Current.Kind == TokenKind.Identifier
            && Peek(1).Kind is TokenKind.Equals or TokenKind.PlusPlus or TokenKind.MinusMinus)
        {
            return ParseAssignment();
        }

        Expression value = ParseExpression();
        return value is MissingExpression ? null : new ExpressionStatement(value.Span, value);
    }

    /// <summary>
    /// <c>name = value</c>, <c>name++</c> or <c>name--</c>.
    /// </summary>
    /// <remarks>
    /// The terminating <c>;</c> is not required, for the same reason a comma between block
    /// entries is not: these appear in a step clause where there is nothing to terminate, and
    /// requiring one in a block and forbidding it there would be a rule with no benefit.
    /// </remarks>
    private Statement ParseAssignment()
    {
        Token name = Advance();
        Token op = Advance();

        if (op.Kind != TokenKind.Equals)
        {
            double by = op.Kind == TokenKind.PlusPlus ? 1 : -1;
            return new IncrementStatement(
                SourceSpan.Union(name.Span, op.Span), name.Text, name.Span, by);
        }

        Expression value = ParseExpression();
        return new AssignmentStatement(
            SourceSpan.Union(name.Span, value.Span), name.Text, name.Span, value);
    }

    /// <param name="terminated">
    /// False in the init clause of a <c>for</c>, where the <c>;</c> belongs to the loop
    /// header rather than to the declaration and consuming it here would eat the separator
    /// the loop is about to look for.
    /// </param>
    private Statement ParseLetStatement(bool terminated = true)
    {
        Token keyword = Advance();
        Token name = Expect(TokenKind.Identifier, "a name after 'let'");
        Expect(TokenKind.Equals, "'='");

        Expression value = ParseExpression();

        if (terminated)
        {
            Expect(TokenKind.Semicolon, "';' at the end of a 'let'");
        }

        SourceSpan span = SourceSpan.Union(keyword.Span, value.Span);
        return new LetStatement(span, name.Text, name.Span, value);
    }

    /// <summary>
    /// Whether the statement reads <c>fn name(…</c>, the declaration form that used to exist.
    /// </summary>
    /// <remarks>
    /// <c>fn</c> is an ordinary identifier now, so this matches on the shape rather than on a
    /// reserved word: three tokens, of which the first spells <c>fn</c> and the third opens a
    /// parameter list. Nothing that means anything else can look like that.
    /// </remarks>
    private bool LooksLikeAnExpressionFunction() =>
        Current.Kind == TokenKind.Identifier
        && Current.Text == "fn"
        && Peek(1).Kind == TokenKind.Identifier
        && Peek(2).Kind == TokenKind.LeftParen;

    private Statement? RejectExpressionFunction()
    {
        Token keyword = Advance();
        Token name = Advance();

        _diagnostics.Error(
            SourceSpan.Union(keyword.Span, name.Span),
            $"'fn {name.Text}(…) = value;' is the declaration form this language used to "
            + $"have; write 'function {name.Text}(…) {{ return value; }}'");

        // Skip the whole declaration. It ends at the ';' a 'fn' always carried, and its body
        // parsed on its own terms would report a second time about the same line.
        while (Current.Kind is not (TokenKind.Semicolon or TokenKind.EndOfFile))
        {
            Advance();
        }

        Match(TokenKind.Semicolon);
        return null;
    }

    /// <summary><c>function name(a, b) { … }</c></summary>
    private Statement ParseFunctionStatement()
    {
        Token keyword = Advance();
        Token name = Expect(TokenKind.Identifier, "a name after 'function'");

        Expect(TokenKind.LeftParen, "'(' after the name of a function");
        List<Parameter> parameters = [];

        while (Current.Kind is not (TokenKind.RightParen or TokenKind.EndOfFile))
        {
            // Commas separate parameters and are optional, as everywhere else in the
            // language. Anything that is neither is reported and skipped, which is also what
            // guarantees this loop makes progress.
            if (Match(TokenKind.Comma))
            {
                continue;
            }

            if (Current.Kind == TokenKind.Identifier)
            {
                Token parameter = Advance();
                parameters.Add(new Parameter(parameter.Text, parameter.Span));
                continue;
            }

            _diagnostics.Error(
                Current.Span, $"expected a parameter name, found {Current.Describe()}");
            Advance();
        }

        Token close = Expect(TokenKind.RightParen, "')' after the parameters");
        (SourceSpan bodySpan, IReadOnlyList<Statement> body) = ParseBody("the body of a function");

        return new FunctionStatement(
            SourceSpan.Union(keyword.Span, bodySpan.Length > 0 ? bodySpan : close.Span),
            name.Text,
            name.Span,
            parameters,
            body);
    }

    private Statement ParseReturnStatement()
    {
        Token keyword = Advance();
        Expression value = ParseExpression();
        Expect(TokenKind.Semicolon, "';' at the end of a 'return'");

        return new ReturnStatement(
            SourceSpan.Union(keyword.Span, value.Span), keyword.Span, value);
    }

    private Statement ParseIfStatement()
    {
        Token keyword = Advance();

        Expect(TokenKind.LeftParen, "'(' after 'if'");
        Expression condition = ParseExpression();
        Expect(TokenKind.RightParen, "')' after the condition");

        (SourceSpan thenSpan, IReadOnlyList<Statement> then) = ParseBody("the body of an 'if'");
        List<Statement> otherwise = [];
        SourceSpan end = thenSpan.Length > 0 ? thenSpan : condition.Span;

        if (Current.Kind == TokenKind.Else)
        {
            Advance();

            // 'else if' chains without a brace, the way every language with this syntax
            // does it: the nested 'if' is the whole of the else body.
            if (Current.Kind == TokenKind.If)
            {
                Statement chained = ParseIfStatement();
                otherwise = [chained];
                end = chained.Span;
            }
            else
            {
                (SourceSpan elseSpan, IReadOnlyList<Statement> body) =
                    ParseBody("the body of an 'else'");

                otherwise = [.. body];
                end = elseSpan.Length > 0 ? elseSpan : end;
            }
        }

        return new IfStatement(SourceSpan.Union(keyword.Span, end), condition, then, otherwise);
    }

    /// <summary><c>for (init; condition; step) { … }</c></summary>
    /// <remarks>
    /// Every clause is optional and the two <c>;</c> are not, which is C's rule and
    /// JavaScript's. An empty condition is <c>true</c>, so <c>for (;;)</c> is the infinite
    /// loop — reported by the evaluator's iteration budget rather than refused here, because
    /// a loop with a condition too clever to read statically fails the same way and only the
    /// budget catches both.
    /// </remarks>
    private Statement ParseForStatement()
    {
        Token keyword = Advance();
        Expect(TokenKind.LeftParen, "'(' after 'for'");

        if (LooksLikeARangeLoop())
        {
            return RejectRangeLoop(keyword);
        }

        Statement? init = ParseClause(TokenKind.Semicolon);
        Expect(TokenKind.Semicolon, "';' after the first clause of a 'for'");

        Expression? condition =
            Current.Kind == TokenKind.Semicolon ? null : ParseExpression();
        Expect(TokenKind.Semicolon, "';' after the condition of a 'for'");

        Statement? step = ParseClause(TokenKind.RightParen);
        Expect(TokenKind.RightParen, "')' after the clauses of a 'for'");

        (SourceSpan bodySpan, IReadOnlyList<Statement> body) = ParseBody("the body of a 'for'");

        return new ForStatement(
            SourceSpan.Union(keyword.Span, bodySpan.Length > 0 ? bodySpan : keyword.Span),
            keyword.Span,
            init,
            condition,
            step,
            body);
    }

    /// <summary>
    /// One clause of a <c>for</c> header, or null if it is empty.
    /// </summary>
    /// <remarks>
    /// The clause is a statement that must not swallow its own terminator, which is why
    /// <c>let</c> is spelled out here rather than left to <see cref="ParseStatement"/>: the
    /// <c>;</c> after <c>let i = 0</c> is the loop's separator, not the declaration's.
    /// </remarks>
    private Statement? ParseClause(TokenKind terminator)
    {
        if (Current.Kind == terminator)
        {
            return null;
        }

        return Current.Kind == TokenKind.Let ? ParseLetStatement(terminated: false) : ParseStatement();
    }

    /// <summary>Whether the header reads <c>for (i in …)</c>, the form that used to exist.</summary>
    private bool LooksLikeARangeLoop() =>
        Current.Kind == TokenKind.Identifier && Peek(1).Kind == TokenKind.In;

    /// <summary>
    /// Reports a range loop and skips it whole.
    /// </summary>
    /// <remarks>
    /// Worth the twenty lines: every scene and every page of the reference used this form
    /// until the JavaScript revision, so a file that predates it is not a typo but a file
    /// written against the previous language, and one message naming the replacement is worth
    /// more than a dozen about an unexpected <c>..</c>.
    /// </remarks>
    private Statement RejectRangeLoop(Token keyword)
    {
        Token variable = Advance();

        _diagnostics.Error(
            SourceSpan.Union(keyword.Span, variable.Span),
            $"'for ({variable.Text} in a..b)' is the loop form this language used to have; "
            + $"write 'for (let {variable.Text} = a; {variable.Text} < b; {variable.Text}++)'");

        // Skip to the ')' that closes the header, then take the body as written, so that
        // whatever it contains is parsed once and reported on its own terms.
        while (Current.Kind is not (TokenKind.RightParen or TokenKind.EndOfFile))
        {
            Advance();
        }

        Match(TokenKind.RightParen);

        // The body of a range loop was usually written without braces, which are mandatory
        // now — so taking it as one statement is what stops the message above being followed
        // by a second one about the same line.
        IReadOnlyList<Statement> body = Current.Kind == TokenKind.LeftBrace
            ? ParseBody("the body of a 'for'").Body
            : ParseStatement() is { } single ? [single] : [];

        return new ForStatement(
            SourceSpan.Union(keyword.Span, body.Count > 0 ? body[^1].Span : keyword.Span),
            keyword.Span,
            null,

            // Never runs: the diagnostic above already means the load fails, and a body that
            // ran would report every mistake inside it a second time.
            new BooleanExpression(keyword.Span, false),
            null,
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
    /// The braced body of an <c>if</c>, a <c>for</c> or a function.
    /// </summary>
    /// <remarks>
    /// The braces are mandatory. They were optional around a single statement until the
    /// JavaScript revision, and the cost of that was one rule to remember and one class of
    /// mistake — <c>if (a) b else c</c> reading as three statements — that no longer has a
    /// way to be written. A <c>{</c> here is always a body and never an object literal.
    /// </remarks>
    private (SourceSpan Span, IReadOnlyList<Statement> Body) ParseBody(string what)
    {
        if (Current.Kind != TokenKind.LeftBrace)
        {
            _diagnostics.Error(
                Current.Span, $"expected '{{' to open {what}, found {Current.Describe()}");

            return (new SourceSpan(Current.Span.Start, 0, Current.Span.Source), []);
        }

        Token open = Advance();
        List<Statement> body = ParseStatements(TokenKind.RightBrace, "the body");
        Token close = Expect(TokenKind.RightBrace, "'}'");

        return (SourceSpan.Union(open.Span, close.Span), body);
    }

    private Expression ParseExpression() => ParseTernary();

    /// <summary>
    /// <c>cond ? a : b</c> — the lowest precedence there is, and right-associative.
    /// </summary>
    /// <remarks>
    /// Right-associative so that <c>a ? b : c ? d : e</c> chains the way an <c>else if</c>
    /// does. The middle arm is a full expression rather than a ternary, which is JavaScript's
    /// rule and is what makes the <c>:</c> unambiguous: it can only close the <c>?</c> that
    /// opened, never a field.
    /// </remarks>
    private Expression ParseTernary()
    {
        Expression condition = ParseOr();

        if (Current.Kind != TokenKind.Question)
        {
            return condition;
        }

        Advance();
        Expression whenTrue = ParseExpression();
        Expect(TokenKind.Colon, "':' between the two arms of a '?'");
        Expression whenFalse = ParseTernary();

        return new ConditionalExpression(
            SourceSpan.Union(condition.Span, whenFalse.Span), condition, whenTrue, whenFalse);
    }

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

        while (Current.Kind is TokenKind.Star or TokenKind.Slash or TokenKind.Percent)
        {
            BinaryOperator op = Advance().Kind switch
            {
                TokenKind.Star => BinaryOperator.Multiply,
                TokenKind.Slash => BinaryOperator.Divide,
                _ => BinaryOperator.Modulo,
            };

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
                return RejectConditionalExpression();

            case TokenKind.LeftBrace:
            {
                (SourceSpan span, IReadOnlyList<Statement> body) = ParseBlock();
                return new ObjectExpression(span, null, default, body);
            }

            case TokenKind.Identifier:
            {
                Token name = Advance();

                if (Current.Kind == TokenKind.LeftParen)
                {
                    return ParseCall(name);
                }

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
    /// Reports an <c>if</c> where a value is wanted, and consumes the whole of it.
    /// </summary>
    /// <remarks>
    /// <c>if (c) a else b</c> was how a value was chosen before the ternary, and
    /// <c>material: if (corner) gold else steel</c> is the shape it took in every generated
    /// scene. Reading it to the end and reporting once is what stops that line producing four
    /// further complaints about the arms it was made of.
    /// </remarks>
    private Expression RejectConditionalExpression()
    {
        Token keyword = Advance();

        _diagnostics.Error(
            keyword.Span,
            "an 'if' is a statement and produces no value; "
            + "write 'condition ? a : b' to choose between two");

        if (Match(TokenKind.LeftParen))
        {
            ParseExpression();
            Match(TokenKind.RightParen);
            ParseExpression();

            if (Match(TokenKind.Else))
            {
                ParseExpression();
            }
        }

        return new MissingExpression(new SourceSpan(keyword.Span.Start, 0, keyword.Span.Source));
    }

    /// <summary>
    /// The argument list of <c>name(...)</c>, the name already consumed.
    /// </summary>
    /// <remarks>
    /// Third and last reading of an identifier: <c>(</c> makes it a call, <c>{</c> a node,
    /// and neither a reference to a binding. All three are settled by one token.
    /// </remarks>
    private Expression ParseCall(Token name)
    {
        Advance();
        List<Expression> arguments = [];

        while (Current.Kind is not (TokenKind.RightParen or TokenKind.EndOfFile))
        {
            int before = _index;

            if (Match(TokenKind.Comma))
            {
                continue;
            }

            arguments.Add(ParseExpression());

            if (_index == before)
            {
                Advance();
            }
        }

        Token close = Expect(TokenKind.RightParen, "')' after the arguments");
        return new CallExpression(
            SourceSpan.Union(name.Span, close.Span), name.Text, name.Span, arguments);
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
