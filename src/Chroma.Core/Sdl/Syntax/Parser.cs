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

            case TokenKind.Import:
                return ParseImportStatement();

            case TokenKind.Include:
                return RejectInclude();

            case TokenKind.Private:
                return ParsePrivateDeclaration();

            case TokenKind.Struct:
                return ParseStructStatement();

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

        if (value is IndexExpression or MemberExpression)
        {
            if (Current.Kind == TokenKind.Equals)
            {
                Advance();
                Expression assigned = ParseExpression();

                return new PathAssignmentStatement(
                    SourceSpan.Union(value.Span, assigned.Span), value, assigned);
            }

            if (Current.Kind is TokenKind.PlusPlus or TokenKind.MinusMinus)
            {
                return RejectStepOnAPart(value);
            }
        }

        return value is MissingExpression ? null : new ExpressionStatement(value.Span, value);
    }

    /// <summary>
    /// Reports <c>a[0]++</c>, which does not exist although <c>a[0] = a[0] + 1</c> does.
    /// </summary>
    /// <remarks>
    /// <c>++</c> steps a *name*, and it exists for the step clause of a <c>for</c> and
    /// essentially nowhere else. Widening it to a path would mean deciding how many times the
    /// index between the brackets is evaluated, which is the question this language avoided by
    /// making <c>++</c> a statement with no value in the first place.
    /// </remarks>
    private Statement? RejectStepOnAPart(Expression target)
    {
        Token op = Advance();
        string written = target.Span.Source?.GetText(target.Span) ?? "a[0]";

        _diagnostics.Error(
            SourceSpan.Union(target.Span, op.Span),
            $"'{op.Text}' steps a name; write '{written} = {written} "
            + $"{(op.Kind == TokenKind.PlusPlus ? "+" : "-")} 1'");

        return null;
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

    /// <summary><c>struct Point { x, y }</c></summary>
    /// <remarks>
    /// The body is a list of names rather than a statement list, which is the one place in the
    /// language where <c>{ … }</c> is neither a block nor an object literal. It is read here
    /// rather than through <see cref="ParseBody"/> for exactly that reason: a field declaration
    /// is a name, and letting the general reader at it would accept statements this has no
    /// meaning for and report them somewhere less useful.
    /// </remarks>
    private Statement ParseStructStatement()
    {
        Token keyword = Advance();
        Token name = Expect(TokenKind.Identifier, "a name after 'struct'");

        List<StructField> fields = [];
        SourceSpan end = name.Span;

        if (Current.Kind != TokenKind.LeftBrace)
        {
            _diagnostics.Error(
                Current.Span,
                $"expected '{{' to open the fields of '{name.Text}', found {Current.Describe()}");

            return new StructStatement(
                SourceSpan.Union(keyword.Span, end), name.Text, name.Span, fields);
        }

        Advance();

        while (Current.Kind is not (TokenKind.RightBrace or TokenKind.EndOfFile))
        {
            // Commas separate fields and are optional, as everywhere else in the language.
            if (Match(TokenKind.Comma) || Match(TokenKind.Semicolon))
            {
                continue;
            }

            if (Current.Kind == TokenKind.Identifier)
            {
                Token field = Advance();
                fields.Add(new StructField(field.Text, field.Span));
                continue;
            }

            _diagnostics.Error(
                Current.Span, $"expected a field name, found {Current.Describe()}");
            Advance();
        }

        Token close = Expect(TokenKind.RightBrace, "'}' after the fields of a 'struct'");

        return new StructStatement(
            SourceSpan.Union(keyword.Span, close.Span), name.Text, name.Span, fields);
    }

    /// <summary><c>import "path";</c> or <c>import "path" as name;</c></summary>
    private Statement ParseImportStatement()
    {
        Token keyword = Advance();
        Token path = Expect(TokenKind.String, "a quoted file name after 'import'");

        string? alias = null;
        SourceSpan aliasSpan = default;
        SourceSpan end = path.Span;

        if (Match(TokenKind.As))
        {
            Token name = Expect(TokenKind.Identifier, "a name after 'as'");
            alias = name.Text;
            aliasSpan = name.Span;
            end = name.Span;
        }

        Expect(TokenKind.Semicolon, "';' at the end of an 'import'");

        return new ImportStatement(
            SourceSpan.Union(keyword.Span, end), path.Text, path.Span, alias, aliasSpan);
    }

    /// <summary>Reports <c>include</c>, and reads the rest of it as the import it was.</summary>
    /// <remarks>
    /// The keyword changed in iteration 20 and nothing else did, so this reports once and then
    /// carries on with the statement rather than skipping it: every later mistake in the file
    /// is still worth finding on the same run.
    /// </remarks>
    private Statement RejectInclude()
    {
        _diagnostics.Error(
            Current.Span,
            "'include' is the keyword this language used to have; write 'import', which is "
            + "what it has always meant");

        return ParseImportStatement();
    }

    /// <summary>
    /// <c>private</c> in front of a <c>let</c>, a <c>function</c> or a <c>struct</c>.
    /// </summary>
    /// <remarks>
    /// The marker is on what stays rather than on what leaves, which is the way round that
    /// leaves the common case unannotated: a file written to be imported is written for its
    /// bindings, and the helpers it does not want to publish are the few.
    /// </remarks>
    private Statement? ParsePrivateDeclaration()
    {
        Token keyword = Advance();

        Statement? declaration = Current.Kind switch
        {
            TokenKind.Let => ParseLetStatement(),
            TokenKind.Function => ParseFunctionStatement(),
            TokenKind.Struct => ParseStructStatement(),
            _ => null,
        };

        if (declaration is null)
        {
            _diagnostics.Error(
                keyword.Span,
                "'private' belongs in front of a 'let', a 'function' or a 'struct', "
                + $"found {Current.Describe()}");

            return null;
        }

        SourceSpan span = SourceSpan.Union(keyword.Span, declaration.Span);

        return declaration switch
        {
            LetStatement let => let with { Span = span, IsPrivate = true },
            FunctionStatement function => function with { Span = span, IsPrivate = true },
            _ => ((StructStatement)declaration) with { Span = span, IsPrivate = true },
        };
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
        Expression left = ParseBitwiseOr();

        while (Current.Kind == TokenKind.AmpersandAmpersand)
        {
            Advance();
            Expression right = ParseBitwiseOr();
            left = new BinaryExpression(
                SourceSpan.Union(left.Span, right.Span), BinaryOperator.And, left, right);
        }

        return left;
    }

    // The three levels below are C's, in C's order and at C's place in the table: '&' binds
    // tighter than '^', which binds tighter than '|', and all three bind tighter than '&&'
    // and looser than '=='. Reproducing that order matters more than liking it — a scene
    // written by someone who knows C must not mean something else here.

    private Expression ParseBitwiseOr()
    {
        Expression left = ParseBitwiseXor();

        while (Current.Kind == TokenKind.Pipe)
        {
            Advance();
            Expression right = ParseBitwiseXor();
            left = new BinaryExpression(
                SourceSpan.Union(left.Span, right.Span), BinaryOperator.BitwiseOr, left, right);
        }

        return left;
    }

    private Expression ParseBitwiseXor()
    {
        Expression left = ParseBitwiseAnd();

        while (Current.Kind == TokenKind.Caret)
        {
            Advance();
            Expression right = ParseBitwiseAnd();
            left = new BinaryExpression(
                SourceSpan.Union(left.Span, right.Span), BinaryOperator.BitwiseXor, left, right);
        }

        return left;
    }

    private Expression ParseBitwiseAnd()
    {
        Expression left = ParseEquality();

        while (Current.Kind == TokenKind.Ampersand)
        {
            Advance();
            Expression right = ParseEquality();
            left = new BinaryExpression(
                SourceSpan.Union(left.Span, right.Span), BinaryOperator.BitwiseAnd, left, right);
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
        Expression left = ParseShift();

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

            Expression right = ParseShift();
            left = new BinaryExpression(SourceSpan.Union(left.Span, right.Span), op, left, right);
        }

        return left;
    }

    /// <summary><c>&lt;&lt;</c> and <c>&gt;&gt;</c>, between the additive level and comparison.</summary>
    /// <remarks>
    /// C's placement, which is the one every C programmer has been bitten by: <c>a &lt;&lt; 1 + 2</c>
    /// shifts by three. Keeping the surprise is better than inventing a second table.
    /// </remarks>
    private Expression ParseShift()
    {
        Expression left = ParseAdditive();

        while (Current.Kind is TokenKind.LessLess or TokenKind.GreaterGreater)
        {
            BinaryOperator op = Advance().Kind == TokenKind.LessLess
                ? BinaryOperator.ShiftLeft
                : BinaryOperator.ShiftRight;

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
        if (Current.Kind is not (TokenKind.Minus or TokenKind.Bang or TokenKind.Tilde))
        {
            return ParsePostfix();
        }

        Token op = Advance();
        Expression operand = ParseUnary();
        SourceSpan span = SourceSpan.Union(op.Span, operand.Span);

        UnaryOperator kind = op.Kind switch
        {
            TokenKind.Minus => UnaryOperator.Negate,
            TokenKind.Bang => UnaryOperator.Not,
            _ => UnaryOperator.Complement,
        };

        return new UnaryExpression(span, kind, operand);
    }

    /// <summary>
    /// A primary followed by any number of <c>[index]</c> and <c>.name</c> suffixes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both bind tighter than every operator and group to the left, so <c>-a[0]</c> negates the
    /// element and <c>a[0].x</c> reads the field of it. That is C's rule and JavaScript's.
    /// </para>
    /// <para>
    /// <b>A <c>[</c> suffix is only read after something that could name an array</b>, which is
    /// what <see cref="IsIndexable"/> decides — and that restriction is load-bearing rather than
    /// tidy. Commas are optional everywhere in this language, so without it
    /// <c>[[0, 0] [1, 0]]</c> would read as one array indexed by another instead of as two
    /// points, and <c>sphere { }</c> followed by <c>[1, 2, 3]</c> on the next line would read as
    /// one indexing expression instead of as two statements. JavaScript closes the same hole
    /// with a newline rule; whitespace is insignificant here, so this closes it by noticing that
    /// nobody indexes a literal.
    /// </para>
    /// <para>
    /// <c>.</c> needs no such guard: it cannot begin a statement or an expression, so it always
    /// belongs to what precedes it.
    /// </para>
    /// </remarks>
    private Expression ParsePostfix()
    {
        Expression target = ParsePrimary();

        while (true)
        {
            if (Current.Kind == TokenKind.LeftBracket && IsIndexable(target))
            {
                Token open = Advance();
                Expression index = ParseExpression();
                Token close = Expect(TokenKind.RightBracket, "']' after an index");

                target = new IndexExpression(
                    SourceSpan.Union(target.Span, close.Span),
                    target,
                    index,
                    SourceSpan.Union(open.Span, close.Span));

                continue;
            }

            if (Current.Kind == TokenKind.Dot)
            {
                Advance();
                Token name = Expect(TokenKind.Identifier, "a field name after '.'");

                // 'materials.stone(tint)' is a call through a module and 'shapes.Post { … }' is
                // an instance of a type reached through one, rather than a field read followed
                // by something else. Both are decided here by one token, exactly as an
                // identifier followed by '(' or '{' is decided in ParsePrimary.
                if (Current.Kind == TokenKind.LeftParen)
                {
                    target = ParseCall(name, target);
                    continue;
                }

                if (Current.Kind == TokenKind.LeftBrace)
                {
                    (SourceSpan blockSpan, IReadOnlyList<Statement> body) = ParseBlock();

                    target = new ObjectExpression(
                        SourceSpan.Union(target.Span, blockSpan), name.Text, name.Span, body, target);

                    continue;
                }

                target = new MemberExpression(
                    SourceSpan.Union(target.Span, name.Span), target, name.Text, name.Span);

                continue;
            }

            return target;
        }
    }

    /// <summary>
    /// Whether a <c>[</c> after this expression indexes it rather than starting a new array.
    /// </summary>
    /// <remarks>
    /// True for the four shapes that can name an array — a binding, a call's result, an element
    /// and a field — and false for every literal, which is what keeps an array literal beside
    /// another one from reading as an index. Nobody writes <c>[1, 2, 3][0]</c>; the parenthesised
    /// form is there for anyone who does.
    /// </remarks>
    private static bool IsIndexable(Expression target) =>
        target is IdentifierExpression or CallExpression or IndexExpression or MemberExpression;

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
                return ParseArray();

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
    private Expression ParseCall(Token name, Expression? target = null)
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
        SourceSpan span = SourceSpan.Union(target?.Span ?? name.Span, close.Span);

        return new CallExpression(span, name.Text, name.Span, arguments, target);
    }

    /// <summary>
    /// <c>[a, b, c]</c>. Elements are full expressions, so an array nests.
    /// </summary>
    private Expression ParseArray()
    {
        Token open = Advance();
        List<Expression> elements = [];

        while (Current.Kind is not (TokenKind.RightBracket or TokenKind.EndOfFile))
        {
            int before = _index;

            if (Match(TokenKind.Comma))
            {
                continue;
            }

            elements.Add(ParseExpression());

            if (_index == before)
            {
                Advance();
            }
        }

        Token close = Expect(TokenKind.RightBracket, "']'");
        return new ArrayExpression(SourceSpan.Union(open.Span, close.Span), elements);
    }

    private (SourceSpan Span, IReadOnlyList<Statement> Body) ParseBlock()
    {
        Token open = Advance();
        List<Statement> body = ParseStatements(TokenKind.RightBrace, "a block");
        Token close = Expect(TokenKind.RightBrace, "'}'");

        return (SourceSpan.Union(open.Span, close.Span), body);
    }
}
