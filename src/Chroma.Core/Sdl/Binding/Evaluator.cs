using System.Globalization;
using Chroma.Core.Sdl.Lexing;
using Chroma.Core.Sdl.Source;
using Chroma.Core.Sdl.Syntax;

namespace Chroma.Core.Sdl.Binding;

/// <summary>
/// Executes a scene file's statements and folds its expressions into
/// <see cref="SdlValue"/>s.
/// </summary>
/// <remarks>
/// <para>
/// Iteration 8 is where this stopped being a fold and became an interpreter. Before it, one
/// statement produced at most one entry and the tree the binder walked was the tree the
/// parser produced. <see cref="Execute"/> now produces a variable number of entries per
/// statement, and how many depends on values that do not exist until it runs.
/// </para>
/// <para>
/// What did not change is where diagnostics point. Every generated entry carries the
/// <see cref="SourceSpan"/> of the text it was generated from, so a mistake inside a loop
/// body is still reported at the line the body is written on — which is the property a
/// textual preprocessor would have given away.
/// </para>
/// </remarks>
/// <param name="seed">
/// The scene's random seed, which <c>random</c> and <c>perlin</c> are functions of. Read out
/// of the file's text by <see cref="SeedReader"/> rather than from the bound <c>render</c>
/// block, because the numbers are drawn long before that block is bound.
/// </param>
public sealed class Evaluator(DiagnosticBag diagnostics, int seed = 0)
{
    /// <summary>
    /// How deeply calls may nest.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The one thing left here that is counted, and it is counted because of what happens at the
    /// limit rather than because of how long a scene may take. The evaluator recurses on the CLR
    /// stack, so a recursion with no base case ends in a
    /// <see cref="StackOverflowException"/> — which cannot be caught, cannot be reported and
    /// cannot be interrupted. It takes the process down with no diagnostic at all, and that is
    /// the one outcome the loader must never have.
    /// </para>
    /// <para>
    /// There were two budgets beside this one, on loop iterations and on total calls, and both
    /// are gone. They ended a file that would not end, at a number chosen when the ceiling on a
    /// scene was the instruction tape and nothing worth writing came near it. That premise
    /// expired: <c>scenes/cube-4.chroma</c> is a scene the renderer draws at three percent of the
    /// instruction budget, and it spends 328,419 iterations building itself. A count large enough
    /// for it is not a guard, so what is left is what every other interpreter offers — a loop
    /// runs until the file's own condition ends it. See documents/roadmap.md, iteration 18.
    /// </para>
    /// </remarks>
    public const int MaxCallDepth = 64;

    private readonly DiagnosticBag _diagnostics = diagnostics;
    private readonly int _seed = seed;

    // Full paths of the files currently open, innermost last, seeded with the scene file so
    // that a file including itself is caught on the first attempt rather than the second.
    private readonly List<string> _includeStack = [FullPathOrEmpty(diagnostics.Source.Path)];

    private int _callDepth;
    private bool _callDepthReported;

    // Set by 'return' and cleared by the call that was waiting for it. Every statement list
    // checks the flag and stops, which is how the value gets past an 'if' body, a loop body
    // and a block on its way out.
    private bool _returning;
    private SdlValue? _returnValue;

    // Functions already reported as falling off the end of their body, so a call in a loop
    // says it once rather than once per iteration.
    private readonly HashSet<FunctionValue> _missingReturnReported = [];

    /// <summary>
    /// A scope for a file to run in: empty, over a frame holding this scene's built-ins.
    /// </summary>
    /// <remarks>
    /// The scene file gets one and so does every included fragment, which is what makes
    /// <c>random</c> mean the same thing in both while neither can see the other's bindings.
    /// </remarks>
    public Scope RootScope() => Builtins.RootScope(_seed);

    /// <summary>
    /// Runs a list of statements, appending what they produce to <paramref name="entries"/>.
    /// </summary>
    /// <remarks>
    /// The statements share <paramref name="scope"/> rather than getting one of their own:
    /// the caller decides whether this list is a new frame, which is what lets an
    /// <c>include</c> run in a sealed one and a loop iteration in a fresh one.
    /// </remarks>
    public void Execute(
        IReadOnlyList<Statement> statements,
        Scope scope,
        List<BoundEntry> entries)
    {
        foreach (Statement statement in statements)
        {
            // A 'return' ends the list it is in and every list enclosing it, up to the call
            // that is waiting for the value.
            if (_returning)
            {
                return;
            }

            switch (statement)
            {
                case LetStatement let:
                    ExecuteLet(let, scope);
                    break;

                case FunctionStatement function:
                    ExecuteFunction(function, scope);
                    break;

                case ReturnStatement returned:
                    ExecuteReturn(returned, scope);
                    break;

                case AssignmentStatement assignment:
                    ExecuteAssignment(assignment, scope);
                    break;

                case IncrementStatement increment:
                    ExecuteIncrement(increment, scope);
                    break;

                case FieldStatement field:
                {
                    SdlValue? value = Evaluate(field.Value, scope);
                    if (value is not null)
                    {
                        entries.Add(new BoundField(field.Span, field.Name, field.NameSpan, value));
                    }

                    break;
                }

                case ExpressionStatement expression:
                {
                    SdlValue? value = Evaluate(expression.Value, scope);
                    if (value is not null)
                    {
                        entries.Add(new BoundChild(expression.Span, value));
                    }

                    break;
                }

                case IfStatement conditional:
                    ExecuteIf(conditional, scope, entries);
                    break;

                case ForStatement loop:
                    ExecuteFor(loop, scope, entries);
                    break;

                case IncludeStatement include:
                    ExecuteInclude(include, scope, entries);
                    break;
            }
        }
    }

    public SdlValue? Evaluate(Expression expression, Scope scope) => expression switch
    {
        NumberExpression number => new NumberValue(number.Span, number.Value),
        StringExpression text => new StringValue(text.Span, text.Value),
        BooleanExpression boolean => new BooleanValue(boolean.Span, boolean.Value),
        VectorExpression vector => EvaluateVector(vector, scope),
        IdentifierExpression identifier => EvaluateIdentifier(identifier, scope),
        CallExpression call => EvaluateCall(call, scope),
        UnaryExpression unary => EvaluateUnary(unary, scope),
        BinaryExpression binary => EvaluateBinary(binary, scope),
        ConditionalExpression conditional => EvaluateConditional(conditional, scope),
        ObjectExpression obj => EvaluateObject(obj, scope),

        // The parser already reported this one; saying so twice helps nobody.
        MissingExpression => null,

        _ => null,
    };

    private void ExecuteLet(LetStatement statement, Scope scope)
    {
        SdlValue? value = Evaluate(statement.Value, scope);
        if (value is null)
        {
            return;
        }

        // Remember where an object came from, so the hierarchy dump can print
        // 'material=red' rather than the material's components.
        if (value is ObjectValue block)
        {
            value = block.WithSourceName(statement.Name);
        }

        Define(statement.Name, value, statement.NameSpan, scope);
    }

    private void ExecuteReturn(ReturnStatement statement, Scope scope)
    {
        if (_callDepth == 0)
        {
            _diagnostics.Error(
                statement.KeywordSpan,
                "a 'return' belongs inside a function; "
                + "a solid at the top level of a file is written on its own");

            return;
        }

        SdlValue? value = Evaluate(statement.Value, scope);

        if (value is null)
        {
            // Already reported. Unwind anyway: the body has said it is finished, and running
            // the rest of it would report mistakes about a call that has already failed.
            _returning = true;
            return;
        }

        _returning = true;
        _returnValue = value;
    }

    /// <summary>
    /// <c>name = value</c>. Never declares: a name has to exist before it can be assigned to.
    /// </summary>
    /// <remarks>
    /// That is the rule JavaScript's <c>let</c> has and its bare assignment does not, and it
    /// is the one worth having here: a scene file's assignment to a name nobody declared is a
    /// misspelling of one that was.
    /// </remarks>
    private void ExecuteAssignment(AssignmentStatement statement, Scope scope)
    {
        if (RejectWriteToBuiltin(statement.Name, statement.NameSpan, scope))
        {
            return;
        }

        SdlValue? value = Evaluate(statement.Value, scope);

        if (value is null)
        {
            return;
        }

        if (value is ObjectValue block)
        {
            value = block.WithSourceName(statement.Name);
        }

        if (!scope.TrySet(statement.Name, value))
        {
            _diagnostics.Error(
                statement.NameSpan,
                $"unknown name '{statement.Name}'; "
                + $"write 'let {statement.Name} = …' to declare it");
        }
    }

    private void ExecuteIncrement(IncrementStatement statement, Scope scope)
    {
        if (RejectWriteToBuiltin(statement.Name, statement.NameSpan, scope))
        {
            return;
        }

        if (!scope.TryGet(statement.Name, out SdlValue current))
        {
            _diagnostics.Error(statement.NameSpan, $"unknown name '{statement.Name}'");
            return;
        }

        if (current is not NumberValue number)
        {
            string op = statement.By > 0 ? "++" : "--";
            _diagnostics.Error(
                statement.Span,
                $"'{op}' steps a number, and '{statement.Name}' is {current.Describe()}");

            return;
        }

        scope.TrySet(statement.Name, new NumberValue(statement.Span, number.Value + statement.By));
    }

    /// <summary>
    /// Binds a <c>function</c> declaration to its name. Nothing runs until it is called.
    /// </summary>
    /// <remarks>
    /// The name is defined before the parameters are checked, so that a parameter sharing it
    /// is caught — and so that the body, evaluated later against this same live scope, can
    /// see the function it belongs to.
    /// </remarks>
    private void ExecuteFunction(FunctionStatement statement, Scope scope)
    {
        FunctionValue function = new(
            statement.Span, statement.Name, statement.Parameters, statement.Body, scope);

        Define(statement.Name, function, statement.NameSpan, scope);

        // Parameters are ordinary bindings and obey the ordinary rule: nothing shadows.
        // Checked once here rather than at every call, which is both where the mistake is
        // written and where it is reported only once.
        HashSet<string> declared = new(StringComparer.Ordinal);

        foreach (Parameter parameter in statement.Parameters)
        {
            if (!declared.Add(parameter.Name))
            {
                _diagnostics.Error(
                    parameter.Span,
                    $"'{parameter.Name}' is already a parameter of '{statement.Name}'");
            }
            else if (scope.Contains(parameter.Name))
            {
                RejectRedefinition(parameter.Name, parameter.Span, scope);
            }
        }
    }

    /// <summary>
    /// Calls a function: <c>name(a, b)</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two scopes are in play and the split is the whole of the semantics. The arguments are
    /// evaluated in the <i>caller's</i> scope, and the body in a fresh frame over the
    /// function's <i>closure</i> — so a function means the same thing wherever it is called
    /// from, which is <c>include</c>'s rule applied one level down.
    /// </para>
    /// <para>
    /// A call returning an object names it after the function, so a hierarchy dump reads
    /// <c>material=tinted</c> rather than the material's components, exactly as a <c>let</c>
    /// does.
    /// </para>
    /// </remarks>
    private SdlValue? EvaluateCall(CallExpression expression, Scope scope)
    {
        if (!scope.TryGet(expression.Name, out SdlValue callee))
        {
            _diagnostics.Error(expression.NameSpan, $"unknown function '{expression.Name}'");
            return null;
        }

        if (callee is BuiltinValue builtin)
        {
            return EvaluateBuiltinCall(expression, builtin, scope);
        }

        if (callee is not FunctionValue function)
        {
            _diagnostics.Error(
                expression.NameSpan,
                $"'{expression.Name}' is {callee.Describe()} and cannot be called");
            return null;
        }

        if (expression.Arguments.Count != function.Parameters.Count)
        {
            _diagnostics.Error(
                expression.Span,
                $"'{function.Name}' takes {Arguments(function.Parameters.Count)}, "
                + $"found {expression.Arguments.Count}");
            return null;
        }

        SdlValue[] arguments = new SdlValue[expression.Arguments.Count];

        for (int i = 0; i < arguments.Length; i++)
        {
            if (Evaluate(expression.Arguments[i], scope) is not { } argument)
            {
                return null;
            }

            arguments[i] = argument;
        }

        if (!TakeCall(expression, function))
        {
            return null;
        }

        Scope frame = function.Closure.Nested();

        for (int i = 0; i < arguments.Length; i++)
        {
            frame.Define(function.Parameters[i].Name, arguments[i]);
        }

        _callDepth++;

        try
        {
            // Entries the body produces at its own level, rather than inside a block within
            // it. There should be none — a function says what it produces with 'return' —
            // and the ones there are name the mistake below.
            List<BoundEntry> stray = [];
            Execute(function.Body, frame, stray);

            SdlValue? result = Take(function, expression, stray);

            return result is ObjectValue { SourceName: null } block
                ? block.WithSourceName(function.Name)
                : result;
        }
        finally
        {
            _callDepth--;
        }
    }

    /// <summary>
    /// Calls a built-in: <c>random(i)</c>.
    /// </summary>
    /// <remarks>
    /// Deliberately not the path above. A built-in has no body, no closure and no frame, so
    /// none of what that path does applies to it — and nothing it does can recurse, which is
    /// why it is not counted against <see cref="MaxCallDepth"/>. What it shares is the arity
    /// message, so that a wrong argument count reads the same whichever kind of function it
    /// was written against.
    /// </remarks>
    private SdlValue? EvaluateBuiltinCall(
        CallExpression expression,
        BuiltinValue builtin,
        Scope scope)
    {
        if (expression.Arguments.Count != builtin.Parameters.Count)
        {
            _diagnostics.Error(
                expression.Span,
                $"'{builtin.Name}' takes {Arguments(builtin.Parameters.Count)}, "
                + $"found {expression.Arguments.Count}");

            return null;
        }

        double[] arguments = new double[builtin.Parameters.Count];

        for (int i = 0; i < arguments.Length; i++)
        {
            if (Evaluate(expression.Arguments[i], scope) is not { } argument)
            {
                return null;
            }

            if (argument is not NumberValue number)
            {
                _diagnostics.Error(
                    argument.Span,
                    $"'{builtin.Parameters[i]}' of '{builtin.Name}' is a number, "
                    + $"found {argument.Describe()}");

                return null;
            }

            arguments[i] = number.Value;
        }

        return new NumberValue(expression.Span, builtin.Apply(arguments));
    }

    /// <summary>
    /// Collects what a body left behind: its returned value, and anything it produced that
    /// a function has no way to use.
    /// </summary>
    private SdlValue? Take(
        FunctionValue function,
        CallExpression call,
        IReadOnlyList<BoundEntry> stray)
    {
        foreach (BoundEntry entry in stray)
        {
            _diagnostics.Error(
                entry.Span,
                entry is BoundField field
                    ? $"'{field.Name}:' is a field and belongs inside a block"
                    : $"this value is not used; '{function.Name}' produces its result "
                      + "with 'return'");
        }

        bool returned = _returning;
        SdlValue? value = _returnValue;

        _returning = false;
        _returnValue = null;

        if (returned)
        {
            return value;
        }

        // Reported at the call, because that is the position that has one, and once per
        // function, because a call inside a loop would otherwise say it every iteration.
        if (_missingReturnReported.Add(function))
        {
            _diagnostics.Error(
                call.NameSpan,
                $"'{function.Name}' reaches the end of its body without a 'return'");
        }

        return null;
    }

    /// <summary>
    /// Whether one more call may nest, reporting it once if it may not.
    /// </summary>
    /// <remarks>
    /// Reported once and then silent. A runaway recursion is one mistake however many calls
    /// meet the limit, and a recursion that branches would otherwise report it thousands of
    /// times over.
    /// </remarks>
    private bool TakeCall(CallExpression expression, FunctionValue function)
    {
        if (_callDepth >= MaxCallDepth)
        {
            if (!_callDepthReported)
            {
                _callDepthReported = true;
                _diagnostics.Error(
                    expression.NameSpan,
                    $"'{function.Name}' is called {MaxCallDepth} calls deep; "
                    + "a function that calls itself needs a case that does not");
            }

            return false;
        }

        return true;
    }

    private static string Arguments(int count) =>
        count == 1 ? "1 argument" : $"{count} arguments";

    private void Define(string name, SdlValue value, SourceSpan where, Scope scope)
    {
        if (scope.Contains(name))
        {
            RejectRedefinition(name, where, scope);
            return;
        }

        scope.Define(name, value);
    }

    /// <summary>
    /// Reports a name that cannot be bound because something already holds it.
    /// </summary>
    /// <remarks>
    /// A built-in is named as one rather than as a definition. Nothing shadows here, so
    /// <c>function random(i)</c> is an error and not an override — but the frame it collides
    /// with is not in the file, and "already defined" would send a reader looking for a
    /// declaration that is not there to find.
    /// </remarks>
    private void RejectRedefinition(string name, SourceSpan where, Scope scope)
    {
        scope.TryGet(name, out SdlValue existing);

        _diagnostics.Error(
            where,
            existing is BuiltinValue
                ? $"'{name}' is a built-in function of the language"
                : $"'{name}' is already defined");
    }

    /// <summary>
    /// Whether a name belongs to a built-in, reporting the attempt to write to it if it does.
    /// </summary>
    private bool RejectWriteToBuiltin(string name, SourceSpan where, Scope scope)
    {
        if (!scope.TryGet(name, out SdlValue value) || value is not BuiltinValue)
        {
            return false;
        }

        _diagnostics.Error(where, $"'{name}' is a built-in function, and nothing assigns to one");
        return true;
    }

    private void ExecuteIf(IfStatement statement, Scope scope, List<BoundEntry> entries)
    {
        if (Condition(statement.Condition, scope) is not { } taken)
        {
            return;
        }

        IReadOnlyList<Statement> body = taken ? statement.Then : statement.Else;
        Execute(body, scope.Nested(), entries);
    }

    /// <summary>
    /// <c>for (init; condition; step) { … }</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two frames, and the split is what makes the loop work. The <b>header</b> frame holds
    /// what <c>init</c> declares and survives every iteration, because the counter has to;
    /// each <b>iteration</b> gets a frame of its own over it, so a <c>let</c> in the body is
    /// fresh each time round rather than colliding with itself on the second pass.
    /// </para>
    /// <para>
    /// Unlike the range form this replaced, the loop is not bounded by construction: the
    /// condition is an arbitrary expression and <c>for (;;)</c> is legal, and nothing here
    /// bounds it either. A loop runs until its own condition ends it, which means a file that
    /// says to loop forever does. See <see cref="MaxCallDepth"/> for what that used to be
    /// traded against and why the trade stopped being worth making.
    /// </para>
    /// </remarks>
    private void ExecuteFor(ForStatement statement, Scope scope, List<BoundEntry> entries)
    {
        Scope header = scope.Nested();

        if (statement.Init is not null)
        {
            Execute([statement.Init], header, entries);
        }

        int before = entries.Count;
        int iterations = 0;

        while (!_returning)
        {
            // An absent condition is 'true', as it is in C. A condition that is not a boolean
            // has already been reported, and stopping is the only way not to report it again
            // on every pass.
            if (statement.Condition is not null)
            {
                if (Condition(statement.Condition, header) is not { } run || !run)
                {
                    break;
                }
            }

            iterations++;

            Execute(statement.Body, header.Nested(), entries);

            if (statement.Step is not null)
            {
                Execute([statement.Step], header, entries);
            }
        }

        // Tagged once for the whole loop rather than once per iteration, because the count is
        // not known until it has finished — which is the one thing the range form gave for
        // free and this one does not.
        TagGenerated(
            entries,
            before,
            new LoopOrigin(statement.KeywordSpan, CounterOf(statement.Init), iterations));
    }

    /// <summary>
    /// The name the loop counts on, for a diagnostic to name the loop by. Null when the init
    /// clause declares nothing, which <c>for (;;)</c> does not.
    /// </summary>
    private static string? CounterOf(Statement? init) => init switch
    {
        LetStatement let => let.Name,
        AssignmentStatement assignment => assignment.Name,
        _ => null,
    };

    /// <summary>
    /// Marks the entries one iteration produced as coming from this loop.
    /// </summary>
    /// <remarks>
    /// Only those not already marked, so a nested loop keeps the innermost attribution — the
    /// count a reader would change first.
    /// </remarks>
    private static void TagGenerated(List<BoundEntry> entries, int from, LoopOrigin origin)
    {
        for (int i = from; i < entries.Count; i++)
        {
            if (entries[i] is not BoundChild { Value: ObjectValue { Generator: null } block } child)
            {
                continue;
            }

            entries[i] = new BoundChild(child.Span, block.WithGenerator(origin));
        }
    }

    /// <summary>
    /// Loads an included file, running it in a frame that cannot see this one.
    /// </summary>
    /// <remarks>
    /// The visibility rule is asymmetric, and each direction earns its keep. <b>Out:</b> the
    /// fragment's <c>let</c> bindings join the includer's scope, because a file of materials
    /// that exports nothing is not worth including. <b>In:</b> the fragment cannot see the
    /// includer's bindings, so it means the same thing wherever it is dropped and cannot be
    /// broken by a host scene that happens to define a name it uses. Parameterising a
    /// fragment is what macros are for, and they are deliberately not in this iteration.
    /// </remarks>
    private void ExecuteInclude(
        IncludeStatement statement,
        Scope scope,
        List<BoundEntry> entries)
    {
        (string path, string? text) = Resolve(statement);

        if (text is null)
        {
            return;
        }

        _includeStack.Add(path);

        try
        {
            // The full path, not the path as written: a relative include is relative to the
            // file that wrote it, and printing it verbatim would name a location that does
            // not exist from wherever the renderer was run.
            SourceText source = new(path, text);
            IReadOnlyList<Token> tokens = Lexer.Tokenize(source, _diagnostics);
            SceneFile file = Parser.Parse(tokens, _diagnostics);

            Scope fragment = RootScope();
            Execute(file.Statements, fragment, entries);

            foreach ((string name, SdlValue value) in fragment.Local)
            {
                if (scope.Contains(name))
                {
                    _diagnostics.Error(
                        statement.PathSpan,
                        $"'{statement.Path}' defines '{name}', which is already defined here");

                    continue;
                }

                scope.Define(name, value);
            }
        }
        finally
        {
            _includeStack.RemoveAt(_includeStack.Count - 1);
        }
    }

    /// <summary>
    /// Resolves an include against the directory of the file that wrote it, and reads it.
    /// </summary>
    /// <remarks>
    /// Relative to the including file rather than to the working directory, so a folder of
    /// fragments that include each other keeps working wherever the renderer is run from.
    /// The scene file named on the command line is the one exception the language already
    /// had, and it stays resolved against the working directory.
    /// </remarks>
    private (string Path, string? Text) Resolve(IncludeStatement statement)
    {
        string includer = statement.PathSpan.Source?.Path ?? _diagnostics.Source.Path;
        string full;

        try
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(includer)) ?? string.Empty;
            full = Path.GetFullPath(Path.Combine(directory, statement.Path));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            _diagnostics.Error(statement.PathSpan, $"'{statement.Path}' is not a usable file name");
            return (string.Empty, null);
        }

        if (_includeStack.Contains(full, StringComparer.OrdinalIgnoreCase))
        {
            _diagnostics.Error(
                statement.PathSpan,
                $"'{statement.Path}' is already being included; includes may not form a cycle");

            return (full, null);
        }

        try
        {
            return (full, File.ReadAllText(full));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _diagnostics.Error(statement.PathSpan, $"cannot read '{full}': {e.Message}");
            return (full, null);
        }
    }

    /// <summary>An expression that has to be a boolean, or null having reported why.</summary>
    private bool? Condition(Expression expression, Scope scope)
    {
        SdlValue? value = Evaluate(expression, scope);

        switch (value)
        {
            case null:
                return null;

            case BooleanValue boolean:
                return boolean.Value;

            default:
                _diagnostics.Error(
                    value.Span,
                    $"a condition must be true or false, found {value.Describe()}");
                return null;
        }
    }


    private SdlValue? EvaluateVector(VectorExpression expression, Scope scope)
    {
        List<double> components = new(expression.Components.Count);

        foreach (Expression component in expression.Components)
        {
            SdlValue? value = Evaluate(component, scope);
            if (value is null)
            {
                return null;
            }

            if (value is not NumberValue number)
            {
                _diagnostics.Error(
                    value.Span,
                    $"a vector component must be a number, found {value.Describe()}");
                return null;
            }

            components.Add(number.Value);
        }

        return new VectorValue(expression.Span, components);
    }

    private SdlValue? EvaluateIdentifier(IdentifierExpression expression, Scope scope)
    {
        if (scope.TryGet(expression.Name, out SdlValue value))
        {
            return value;
        }

        _diagnostics.Error(expression.Span, $"unknown name '{expression.Name}'");
        return null;
    }

    private SdlValue? EvaluateUnary(UnaryExpression expression, Scope scope)
    {
        SdlValue? operand = Evaluate(expression.Operand, scope);

        if (operand is null)
        {
            return null;
        }

        if (expression.Operator == UnaryOperator.Not)
        {
            if (operand is BooleanValue boolean)
            {
                return new BooleanValue(expression.Span, !boolean.Value);
            }

            _diagnostics.Error(expression.Span, $"cannot negate {operand.Describe()} with '!'");
            return null;
        }

        if (expression.Operator == UnaryOperator.Complement)
        {
            // '!' is the boolean one and this is the numeric one, which is C's split. A
            // boolean here is almost always '!' misspelled, and the message above says so
            // the other way round.
            return Whole(operand, "~", "a whole number") is { } bits
                ? new NumberValue(expression.Span, ~bits)
                : null;
        }

        return operand switch
        {
            NumberValue number => new NumberValue(expression.Span, -number.Value),
            VectorValue vector =>
                new VectorValue(expression.Span, [.. vector.Components.Select(c => -c)]),
            _ => Reject(expression.Span, $"cannot negate {operand.Describe()}"),
        };
    }

    private SdlValue? EvaluateConditional(ConditionalExpression expression, Scope scope)
    {
        if (Condition(expression.Condition, scope) is not { } taken)
        {
            return null;
        }

        // Only the branch taken is evaluated, so 'if (n > 0) total / n else 0' is safe and
        // an unused branch cannot report a diagnostic about a value nobody asked for.
        return Evaluate(taken ? expression.WhenTrue : expression.WhenFalse, scope);
    }

    private SdlValue? EvaluateBinary(BinaryExpression expression, Scope scope)
    {
        // Short-circuiting, and therefore before either side is evaluated.
        if (expression.Operator is BinaryOperator.And or BinaryOperator.Or)
        {
            if (Condition(expression.Left, scope) is not { } left)
            {
                return null;
            }

            bool shortCircuits = expression.Operator == BinaryOperator.And ? !left : left;
            if (shortCircuits)
            {
                return new BooleanValue(expression.Span, left);
            }

            return Condition(expression.Right, scope) is { } right
                ? new BooleanValue(expression.Span, right)
                : null;
        }

        SdlValue? leftValue = Evaluate(expression.Left, scope);
        SdlValue? rightValue = Evaluate(expression.Right, scope);

        if (leftValue is null || rightValue is null)
        {
            return null;
        }

        if (expression.Operator is BinaryOperator.Equal or BinaryOperator.NotEqual)
        {
            return EvaluateEquality(expression, leftValue, rightValue);
        }

        if (expression.Operator is BinaryOperator.Less or BinaryOperator.LessOrEqual
            or BinaryOperator.Greater or BinaryOperator.GreaterOrEqual)
        {
            return EvaluateOrdering(expression, leftValue, rightValue);
        }

        if (expression.Operator is BinaryOperator.BitwiseAnd or BinaryOperator.BitwiseOr
            or BinaryOperator.BitwiseXor or BinaryOperator.ShiftLeft
            or BinaryOperator.ShiftRight)
        {
            return EvaluateBitwise(expression, leftValue, rightValue);
        }

        return EvaluateArithmetic(expression, leftValue, rightValue);
    }

    /// <summary>
    /// <c>==</c> and <c>!=</c>, over every value kind that has an identity.
    /// </summary>
    /// <remarks>
    /// Objects do not: two <c>sphere { radius: 1 }</c> blocks describe the same solid and are
    /// still two blocks, and there is no reading of "equal" here that a scene file would want.
    /// Comparing values of two different kinds is a mistake rather than a false, so it is
    /// reported instead of answered.
    /// </remarks>
    private SdlValue? EvaluateEquality(BinaryExpression expression, SdlValue left, SdlValue right)
    {
        bool? equal = (left, right) switch
        {
            (NumberValue a, NumberValue b) => a.Value == b.Value,
            (StringValue a, StringValue b) => string.Equals(a.Value, b.Value, StringComparison.Ordinal),
            (BooleanValue a, BooleanValue b) => a.Value == b.Value,
            (VectorValue a, VectorValue b) => a.Components.SequenceEqual(b.Components),
            _ => null,
        };

        if (equal is null)
        {
            return Reject(
                expression.Span,
                $"cannot compare {left.Describe()} with {right.Describe()}");
        }

        bool result = expression.Operator == BinaryOperator.Equal ? equal.Value : !equal.Value;
        return new BooleanValue(expression.Span, result);
    }

    /// <summary>
    /// <c>&lt;</c>, <c>&lt;=</c>, <c>&gt;</c> and <c>&gt;=</c>, over numbers only.
    /// </summary>
    /// <remarks>
    /// Vectors have no order worth guessing at — comparing all three components, or their
    /// lengths, are both defensible and a file cannot say which it meant. Strings have one,
    /// and it would be the only thing in the language that treats a string as text rather
    /// than as the name of a variant.
    /// </remarks>
    private SdlValue? EvaluateOrdering(BinaryExpression expression, SdlValue left, SdlValue right)
    {
        if (left is not NumberValue a || right is not NumberValue b)
        {
            SdlValue offender = left is NumberValue ? right : left;
            return Reject(
                offender.Span,
                $"'{Symbol(expression.Operator)}' compares numbers, found {offender.Describe()}");
        }

        bool result = expression.Operator switch
        {
            BinaryOperator.Less => a.Value < b.Value,
            BinaryOperator.LessOrEqual => a.Value <= b.Value,
            BinaryOperator.Greater => a.Value > b.Value,
            _ => a.Value >= b.Value,
        };

        return new BooleanValue(expression.Span, result);
    }

    /// <summary>
    /// <c>&amp;</c>, <c>|</c>, <c>^</c>, <c>&lt;&lt;</c> and <c>&gt;&gt;</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The first three carry both of C's readings, chosen by the operands: two booleans give
    /// the logical connective, two whole numbers the bitwise one. Nothing mixes the kinds,
    /// which is what lets one spelling serve both without an ambiguity ever arising.
    /// </para>
    /// <para>
    /// <b>The numeric side is a constraint, not a type.</b> The language has one numeric kind
    /// and it is a 64-bit float, so <c>1.5 &amp; 1</c> has no reading a file could have meant
    /// and is reported rather than truncated — the same choice <see cref="BlockReader.Integer"/>
    /// makes for a field. The magnitude is bounded for the same reason: past 2^53 a double
    /// stops holding every whole number, so the answer would not be the answer.
    /// </para>
    /// <para>
    /// Vectors are refused throughout. Arithmetic broadcasts across one because a coordinate
    /// scaled is still a coordinate; a bit pattern per component is not something a scene has
    /// ever wanted, and inventing it here would be a rule with no user.
    /// </para>
    /// </remarks>
    private SdlValue? EvaluateBitwise(BinaryExpression expression, SdlValue left, SdlValue right)
    {
        string symbol = Symbol(expression.Operator);

        bool connective = expression.Operator
            is BinaryOperator.BitwiseAnd or BinaryOperator.BitwiseOr or BinaryOperator.BitwiseXor;

        if (connective && left is BooleanValue a && right is BooleanValue b)
        {
            // Both sides were evaluated before this ran, which is the whole difference from
            // '&&' and '||' and the reason C keeps both spellings.
            bool result = expression.Operator switch
            {
                BinaryOperator.BitwiseAnd => a.Value & b.Value,
                BinaryOperator.BitwiseOr => a.Value | b.Value,
                _ => a.Value ^ b.Value,
            };

            return new BooleanValue(expression.Span, result);
        }

        string expects = connective
            ? "two booleans or two whole numbers"
            : "whole numbers";

        if (connective && (left is BooleanValue || right is BooleanValue))
        {
            // One of each. Reported here rather than falling through, because the message the
            // fall-through would give names the number as the mistake when either side could be.
            return Reject(
                expression.Span,
                $"'{symbol}' takes {expects}, found {left.Describe()} and {right.Describe()}");
        }

        if (Whole(left, symbol, expects) is not { } x || Whole(right, symbol, expects) is not { } y)
        {
            return null;
        }

        if (!connective)
        {
            return EvaluateShift(expression, symbol, x, y);
        }

        long bits = expression.Operator switch
        {
            BinaryOperator.BitwiseAnd => x & y,
            BinaryOperator.BitwiseOr => x | y,
            _ => x ^ y,
        };

        // No range check: the three connectives take the operands' sign extension apart and
        // put it back, so a result outside the range both operands were checked against
        // cannot arise. Only a shift can leave it, and that is checked where it can.
        return new NumberValue(expression.Span, bits);
    }

    /// <summary>
    /// <c>&lt;&lt;</c> and <c>&gt;&gt;</c> on two whole numbers already in range.
    /// </summary>
    /// <remarks>
    /// The two things C leaves undefined are reported here instead. A shift of 64 places or
    /// more, or of a negative count, has no answer worth guessing at; and a left shift is the
    /// one operator that can carry a valid pair of operands past the exact range, so its
    /// result is checked as well as its inputs. <c>&gt;&gt;</c> is arithmetic, so it keeps the
    /// sign, which is C's behaviour on a signed operand and the only one a scene would expect.
    /// </remarks>
    private SdlValue? EvaluateShift(BinaryExpression expression, string symbol, long value, long by)
    {
        if (by is < 0 or > 63)
        {
            return Reject(
                expression.Right.Span,
                $"'{symbol}' shifts by 0 to 63 places, found {by}");
        }

        long shifted = expression.Operator == BinaryOperator.ShiftLeft
            ? value << (int)by
            : value >> (int)by;

        if (Math.Abs(shifted) > ExactWholeLimit)
        {
            return Reject(
                expression.Span,
                $"'{symbol}' takes {Printed(value)} past the largest whole number a scene "
                + "can hold exactly");
        }

        return new NumberValue(expression.Span, shifted);
    }

    /// <summary>
    /// The largest magnitude at which a 64-bit float still holds every whole number: 2^53.
    /// </summary>
    private const double ExactWholeLimit = 9007199254740992.0;

    /// <summary>
    /// A value as a whole number for a bitwise operator, or null having reported why not.
    /// </summary>
    private long? Whole(SdlValue value, string symbol, string expects)
    {
        if (value is not NumberValue number)
        {
            Reject(value.Span, $"'{symbol}' takes {expects}, found {value.Describe()}");
            return null;
        }

        // NaN fails this too, since it equals nothing including its own floor.
        if (number.Value != Math.Floor(number.Value))
        {
            Reject(value.Span, $"'{symbol}' takes {expects}, found {Printed(number.Value)}");
            return null;
        }

        if (Math.Abs(number.Value) > ExactWholeLimit)
        {
            Reject(
                value.Span,
                $"'{symbol}' takes {expects}, and {Printed(number.Value)} is past the largest "
                + "whole number a scene can hold exactly");
            return null;
        }

        return (long)number.Value;
    }

    /// <summary>
    /// A number as a diagnostic prints it, in the invariant culture.
    /// </summary>
    /// <remarks>
    /// Invariant for the reason every conversion in this project is: a message about a file
    /// that writes <c>1.5</c> must not answer with <c>1,5</c> on a machine whose culture does.
    /// </remarks>
    private static string Printed(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);

    private SdlValue? EvaluateArithmetic(BinaryExpression expression, SdlValue left, SdlValue right)
    {
        // Only numbers and vectors have arithmetic. Naming which operand is at fault matters
        // more than it looks: `a + b` where one of them is a 'let' binding of the wrong kind
        // is otherwise a message about a line that reads perfectly well.
        foreach (SdlValue operand in new[] { left, right })
        {
            if (operand is NumberValue or VectorValue)
            {
                continue;
            }

            string what = operand switch
            {
                ObjectValue => "objects",
                BooleanValue => "booleans",
                FunctionValue or BuiltinValue => "functions",
                _ => "strings",
            };

            return Reject(operand.Span, $"{what} do not support arithmetic");
        }

        Func<double, double, double> apply = expression.Operator switch
        {
            BinaryOperator.Add => static (a, b) => a + b,
            BinaryOperator.Subtract => static (a, b) => a - b,
            BinaryOperator.Multiply => static (a, b) => a * b,
            BinaryOperator.Divide => static (a, b) => a / b,

            // C#'s '%' on doubles is C's and JavaScript's: the result takes the sign of the
            // left operand, so '-1 % 2' is -1. Nothing here has to convert to an integer,
            // which is what keeps '1.5 % 1' meaningful.
            _ => static (a, b) => a % b,
        };

        if (left is NumberValue leftNumber && right is NumberValue rightNumber)
        {
            return new NumberValue(expression.Span, apply(leftNumber.Value, rightNumber.Value));
        }

        // Anything else mixes a vector in, so the result is a vector. A scalar operand is
        // broadcast to every component; two vectors combine component by component and
        // must therefore agree in length.
        IReadOnlyList<double> leftComponents = AsComponents(left);
        IReadOnlyList<double> rightComponents = AsComponents(right);
        int count = Math.Max(leftComponents.Count, rightComponents.Count);

        if (leftComponents.Count != rightComponents.Count
            && leftComponents.Count != 1
            && rightComponents.Count != 1)
        {
            return Reject(
                expression.Span,
                $"cannot combine {left.Describe()} with {right.Describe()}");
        }

        double[] result = new double[count];
        for (int i = 0; i < count; i++)
        {
            double a = leftComponents.Count == 1 ? leftComponents[0] : leftComponents[i];
            double b = rightComponents.Count == 1 ? rightComponents[0] : rightComponents[i];
            result[i] = apply(a, b);
        }

        return new VectorValue(expression.Span, result);
    }

    /// <summary>How an operator is written, for a diagnostic to quote it back.</summary>
    private static string Symbol(BinaryOperator op) => op switch
    {
        BinaryOperator.Less => "<",
        BinaryOperator.LessOrEqual => "<=",
        BinaryOperator.Greater => ">",
        BinaryOperator.GreaterOrEqual => ">=",
        BinaryOperator.BitwiseAnd => "&",
        BinaryOperator.BitwiseOr => "|",
        BinaryOperator.BitwiseXor => "^",
        BinaryOperator.ShiftLeft => "<<",
        _ => ">>",
    };

    private static IReadOnlyList<double> AsComponents(SdlValue value) => value switch
    {
        NumberValue number => [number.Value],
        VectorValue vector => vector.Components,
        _ => [],
    };

    private SdlValue? EvaluateObject(ObjectExpression expression, Scope scope)
    {
        // A block is a frame: a 'let' written inside one belongs to it, which is what keeps
        // a helper value next to the geometry that uses it.
        List<BoundEntry> entries = [];
        Execute(expression.Body, scope.Nested(), entries);

        return new ObjectValue(
            expression.Span,
            expression.TypeName,
            expression.TypeNameSpan,
            entries);
    }

    private SdlValue? Reject(SourceSpan span, string message)
    {
        _diagnostics.Error(span, message);
        return null;
    }

    /// <summary>
    /// A path made absolute, or empty if it cannot be. Only ever used for identity, so a
    /// name too strange to resolve simply never matches anything.
    /// </summary>
    private static string FullPathOrEmpty(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Empty;
        }
    }
}
