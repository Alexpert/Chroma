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
/// <param name="nodeNames">
/// The node types the binders understand, which a <c>struct</c> may not take the name of. The
/// evaluator knows nothing else about them: a node name is not a binding and is never looked
/// up here, so this set exists purely to refuse a declaration that would shadow one.
/// </param>
public sealed class Evaluator(
    DiagnosticBag diagnostics,
    int seed = 0,
    IReadOnlySet<string>? nodeNames = null)
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

    private readonly IReadOnlySet<string> _nodeNames =
        nodeNames ?? new HashSet<string>(StringComparer.Ordinal);

    // Full paths of the files currently open, innermost last, seeded with the scene file so
    // that a file importing itself is caught on the first attempt rather than the second.
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
    /// The scene file gets one and so does every imported file, which is what makes
    /// <c>random</c> mean the same thing in both while neither can see the other's bindings.
    /// </remarks>
    public Scope RootScope() => Builtins.RootScope(_seed);

    /// <summary>
    /// Runs a list of statements, appending what they produce to <paramref name="entries"/>.
    /// </summary>
    /// <remarks>
    /// The statements share <paramref name="scope"/> rather than getting one of their own:
    /// the caller decides whether this list is a new frame, which is what lets an
    /// <c>import</c> run in a sealed one and a loop iteration in a fresh one.
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

                case PathAssignmentStatement assignment:
                    ExecutePathAssignment(assignment, scope);
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
                        AddChild(entries, expression.Span, value);
                    }

                    break;
                }

                case IfStatement conditional:
                    ExecuteIf(conditional, scope, entries);
                    break;

                case ForStatement loop:
                    ExecuteFor(loop, scope, entries);
                    break;

                case StructStatement declaration:
                    ExecuteStruct(declaration, scope);
                    break;

                case ImportStatement import:
                    ExecuteImport(import, scope, entries);
                    break;
            }
        }
    }

    /// <summary>
    /// Appends one child to a block, splicing an array into the elements it holds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>An array in child position contributes its elements.</b> That is the asymmetry with a
    /// field, which keeps whatever it is given: a field has a name and a declared meaning, so
    /// <c>points: [[0, 0], [1, 0]]</c> is one list and must stay one. A child position means
    /// "a thing that belongs here", and a list of things that belong here belongs here.
    /// </para>
    /// <para>
    /// It flattens all the way down, which costs nothing to explain and covers a list of rows:
    /// an array was never a valid child on its own, so there is no case where leaving one
    /// unspliced would have been what a file meant.
    /// </para>
    /// </remarks>
    private static void AddChild(List<BoundEntry> entries, SourceSpan span, SdlValue value)
    {
        if (value is not ArrayValue array)
        {
            entries.Add(new BoundChild(span, value));
            return;
        }

        foreach (SdlValue element in array.Elements)
        {
            AddChild(entries, span, element);
        }
    }

    public SdlValue? Evaluate(Expression expression, Scope scope) => expression switch
    {
        NumberExpression number => new NumberValue(number.Span, number.Value),
        StringExpression text => new StringValue(text.Span, text.Value),
        BooleanExpression boolean => new BooleanValue(boolean.Span, boolean.Value),
        ArrayExpression array => EvaluateArray(array, scope),
        IndexExpression index => EvaluateIndex(index, scope),
        MemberExpression member => EvaluateMember(member, scope),
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

        Define(statement.Name, value, statement.NameSpan, scope, statement.IsPrivate);
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

    /// <summary>
    /// <c>a[0] = value</c> and <c>p.x = value</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Nothing is mutated.</b> The path is walked, a new container is built at every step
    /// that leads to the change, and the result is assigned back to the root binding — so an
    /// array and a struct are still values, and no other binding can observe the write. That is
    /// what makes <c>let q = p; q.x = 5;</c> leave <c>p</c> alone, and it is the answer this
    /// language already gives for solids, where referencing a binding twice instantiates it
    /// twice. Choosing the other answer would have made these the only values here with an
    /// identity that survives being passed around.
    /// </para>
    /// <para>
    /// The cost is a copy of each container along the path, which is what an immutable value
    /// model buys the rest of the evaluator: nothing else has to defend against aliasing.
    /// </para>
    /// </remarks>
    private void ExecutePathAssignment(PathAssignmentStatement statement, Scope scope)
    {
        // Innermost first as written, so the list is reversed into the order it is walked.
        List<Expression> steps = [];
        Expression current = statement.Target;

        while (current is IndexExpression or MemberExpression)
        {
            steps.Add(current);
            current = current is IndexExpression index
                ? index.Target
                : ((MemberExpression)current).Target;
        }

        steps.Reverse();

        if (current is not IdentifierExpression root)
        {
            _diagnostics.Error(
                current.Span,
                "the left of an assignment has to start with a name; "
                + "there is nothing here to assign to");

            return;
        }

        if (RejectWriteToBuiltin(root.Name, root.Span, scope))
        {
            return;
        }

        if (!scope.TryGet(root.Name, out SdlValue held))
        {
            _diagnostics.Error(
                root.Span,
                $"unknown name '{root.Name}'; write 'let {root.Name} = …' to declare it");

            return;
        }

        if (Evaluate(statement.Value, scope) is not { } replacement)
        {
            return;
        }

        if (Rebuild(held, steps, 0, replacement, scope) is { } rebuilt)
        {
            scope.TrySet(root.Name, rebuilt);
        }
    }

    /// <summary>
    /// A copy of <paramref name="target"/> with the value at the remaining path replaced.
    /// </summary>
    private SdlValue? Rebuild(
        SdlValue target,
        List<Expression> steps,
        int at,
        SdlValue replacement,
        Scope scope)
    {
        if (at == steps.Count)
        {
            return replacement;
        }

        switch (steps[at])
        {
            case IndexExpression step:
            {
                if (target is not ArrayValue array)
                {
                    return Reject(step.BracketSpan, $"cannot index {target.Describe()}");
                }

                if (ResolveIndex(array, step, scope) is not { } index)
                {
                    return null;
                }

                if (Rebuild(array.Elements[index], steps, at + 1, replacement, scope) is not { } inner)
                {
                    return null;
                }

                SdlValue[] elements = [.. array.Elements];
                elements[index] = inner;
                return new ArrayValue(target.Span, elements);
            }

            default:
            {
                MemberExpression step = (MemberExpression)steps[at];

                if (target is ArrayValue)
                {
                    // 'length' is computed rather than stored, and no other member exists, so
                    // there is nothing on an array a file could assign to.
                    return Reject(
                        step.NameSpan,
                        $"an array has no '{step.Name}' to assign to; "
                        + "'length' is the only one it has, and it is not a field");
                }

                if (target is not StructValue instance)
                {
                    return Reject(step.NameSpan, $"{target.Describe()} has no fields");
                }

                if (!instance.Fields.TryGetValue(step.Name, out SdlValue? held))
                {
                    return Reject(
                        step.NameSpan,
                        $"'{instance.Type.Name}' has no field '{step.Name}'; "
                        + $"it has {Fields(instance.Type)}");
                }

                if (Rebuild(held, steps, at + 1, replacement, scope) is not { } inner)
                {
                    return null;
                }

                Dictionary<string, SdlValue> fields = new(instance.Fields, StringComparer.Ordinal)
                {
                    [step.Name] = inner,
                };

                return new StructValue(target.Span, instance.Type, fields);
            }
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

        Define(statement.Name, function, statement.NameSpan, scope, statement.IsPrivate);

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
    /// Binds a <c>struct</c> declaration to its name. Declares a type; builds nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The type is an ordinary binding, which is the same trick <c>function</c> plays and buys
    /// the same things: no shadowing, one frame per block, and an included fragment exporting
    /// its record types alongside its materials for free.
    /// </para>
    /// <para>
    /// <b>A node name is refused.</b> An instance is written <c>Point { x: 1 }</c>, which is the
    /// syntax a node is written in, so <c>struct sphere { … }</c> would quietly turn every
    /// <c>sphere</c> block in the file into a record and leave the diagnostics to complain
    /// several stages later about a value that cannot be a solid. Reported at the declaration
    /// instead, which is the line to change.
    /// </para>
    /// </remarks>
    private void ExecuteStruct(StructStatement statement, Scope scope)
    {
        if (_nodeNames.Contains(statement.Name))
        {
            _diagnostics.Error(
                statement.NameSpan,
                $"'{statement.Name}' is the name of a node type, so a struct cannot take it");

            return;
        }

        HashSet<string> declared = new(StringComparer.Ordinal);
        List<string> fields = [];

        foreach (StructField field in statement.Fields)
        {
            if (!declared.Add(field.Name))
            {
                _diagnostics.Error(
                    field.Span, $"'{field.Name}' is already a field of '{statement.Name}'");

                continue;
            }

            fields.Add(field.Name);
        }

        Define(
            statement.Name,
            new StructTypeValue(statement.Span, statement.Name, fields),
            statement.NameSpan,
            scope,
            statement.IsPrivate);
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
        SdlValue? callee = expression.Target is null
            ? Resolve(expression, scope)
            : ResolveThroughModule(expression, scope);

        if (callee is null)
        {
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

        // Ahead of evaluating the arguments, and so ahead of Invoke's own check on the same
        // thing: a call written with the wrong number of arguments is one mistake, and the
        // arguments themselves may hold several more that are only consequences of it.
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

        return Invoke(function, arguments, expression.Span, expression.NameSpan);
    }

    /// <summary>
    /// Calls a function with its arguments already in hand, for a binder that samples one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The tail of <see cref="EvaluateCall"/>, which is all a binder wants: it holds the callee
    /// and the argument <i>values</i>, so resolving a name and evaluating expressions in a
    /// caller's scope have already happened or never applied. <c>heightField</c> is the one node
    /// that needs it, and it needs it a million times in a row.
    /// </para>
    /// <para>
    /// <b>Re-entering the evaluator after binding has started is safe</b>, which is worth stating
    /// because nothing else in this file does it. Binding runs after <see cref="Execute"/> has
    /// returned, so <c>_returning</c> is false, <c>_returnValue</c> is null and
    /// <c>_callDepth</c> is zero, and this method leaves all three exactly as it found them.
    /// <c>_missingReturnReported</c> is keyed on the function, so a body that falls off its end
    /// reports once over a whole grid rather than once per sample.
    /// </para>
    /// <para>
    /// The two spans are separate for the reason the expression path already had them separate:
    /// an arity message points at the whole call and a depth message at the name.
    /// </para>
    /// </remarks>
    public SdlValue? Invoke(
        SdlValue callee,
        IReadOnlyList<SdlValue> arguments,
        SourceSpan callSpan,
        SourceSpan nameSpan)
    {
        if (callee is BuiltinValue builtin)
        {
            return ApplyBuiltin(builtin, arguments, callSpan);
        }

        if (callee is not FunctionValue function)
        {
            _diagnostics.Error(nameSpan, $"{callee.Describe()} cannot be called");
            return null;
        }

        if (arguments.Count != function.Parameters.Count)
        {
            _diagnostics.Error(
                callSpan,
                $"'{function.Name}' takes {Arguments(function.Parameters.Count)}, "
                + $"found {arguments.Count}");

            return null;
        }

        if (!TakeCall(nameSpan, function))
        {
            return null;
        }

        Scope frame = function.Closure.Nested();

        for (int i = 0; i < arguments.Count; i++)
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

            SdlValue? result = Take(function, nameSpan, stray);

            return result is ObjectValue { SourceName: null } block
                ? block.WithSourceName(function.Name)
                : result;
        }
        finally
        {
            _callDepth--;
        }
    }

    /// <summary>The value a bare <c>name(…)</c> calls, or null having reported why not.</summary>
    private SdlValue? Resolve(CallExpression expression, Scope scope)
    {
        if (scope.TryGet(expression.Name, out SdlValue callee))
        {
            return callee;
        }

        _diagnostics.Error(expression.NameSpan, $"unknown function '{expression.Name}'");
        return null;
    }

    /// <summary>
    /// The value a qualified <c>module.name(…)</c> calls.
    /// </summary>
    /// <remarks>
    /// A call through a module is decided by the parser, one token after the <c>.</c>, exactly
    /// as an identifier followed by <c>(</c> is. It is not a method call and never becomes one:
    /// the target has to be a module, and nothing is bound to a first parameter.
    /// </remarks>
    private SdlValue? ResolveThroughModule(CallExpression expression, Scope scope)
    {
        if (Evaluate(expression.Target!, scope) is not { } holder)
        {
            return null;
        }

        if (holder is not ModuleValue module)
        {
            _diagnostics.Error(
                expression.Target!.Span,
                $"{holder.Describe()} is not a module, so '{expression.Name}' cannot be "
                + "called through it");

            return null;
        }

        if (module.Exports.TryGetValue(expression.Name, out SdlValue? export))
        {
            return export;
        }

        _diagnostics.Error(
            expression.NameSpan,
            $"'{module.Path}' does not export '{expression.Name}'"
            + (module.Exports.Count == 0
                ? "; it exports nothing"
                : $"; it exports {Names(module.Exports.Keys)}"));

        return null;
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

        SdlValue[] arguments = new SdlValue[builtin.Parameters.Count];

        for (int i = 0; i < arguments.Length; i++)
        {
            if (Evaluate(expression.Arguments[i], scope) is not { } argument)
            {
                return null;
            }

            // Checked as each one arrives rather than after they all have, so that a bad
            // argument is reported before a later one is even evaluated. That is the order this
            // path has always reported in.
            if (!Accepts(builtin, i, argument))
            {
                return null;
            }

            arguments[i] = argument;
        }

        return builtin.Apply(new BuiltinCall(expression.Span, arguments, _diagnostics.Error));
    }

    /// <summary>
    /// Checks a built-in's arguments against its declared kinds and applies it.
    /// </summary>
    /// <remarks>
    /// Split out of <see cref="EvaluateBuiltinCall"/> so that <see cref="Invoke"/> can reach it
    /// with values rather than expressions. It is what makes <c>height: perlin</c> legal beside
    /// <c>height: terrain</c>: a built-in is a value like any other, and a field that takes a
    /// function should not care which kind it was handed.
    /// </remarks>
    private SdlValue? ApplyBuiltin(
        BuiltinValue builtin,
        IReadOnlyList<SdlValue> arguments,
        SourceSpan callSpan)
    {
        if (arguments.Count != builtin.Parameters.Count)
        {
            _diagnostics.Error(
                callSpan,
                $"'{builtin.Name}' takes {Arguments(builtin.Parameters.Count)}, "
                + $"found {arguments.Count}");

            return null;
        }

        for (int i = 0; i < arguments.Count; i++)
        {
            if (!Accepts(builtin, i, arguments[i]))
            {
                return null;
            }
        }

        return builtin.Apply(new BuiltinCall(callSpan, [.. arguments], _diagnostics.Error));
    }

    /// <summary>
    /// Whether one argument is what a built-in declared it takes, reporting it if it is not.
    /// </summary>
    private bool Accepts(BuiltinValue builtin, int index, SdlValue argument)
    {
        BuiltinParameter parameter = builtin.Parameters[index];

        bool matches = parameter.Kind switch
        {
            BuiltinArgument.Number => argument is NumberValue,

            // The language's vector: an array whose elements are all numbers. A nested one is
            // refused here rather than inside a body, so every function that takes a vector says
            // it the same way.
            _ => argument is ArrayValue array && array.AsNumbers() is not null,
        };

        if (matches)
        {
            return true;
        }

        string expected = parameter.Kind == BuiltinArgument.Number ? "a number" : "a vector";

        _diagnostics.Error(
            argument.Span,
            $"'{parameter.Name}' of '{builtin.Name}' is {expected}, found {argument.Describe()}");

        return false;
    }

    /// <summary>
    /// Collects what a body left behind: its returned value, and anything it produced that
    /// a function has no way to use.
    /// </summary>
    private SdlValue? Take(
        FunctionValue function,
        SourceSpan nameSpan,
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
                nameSpan,
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
    private bool TakeCall(SourceSpan nameSpan, FunctionValue function)
    {
        if (_callDepth >= MaxCallDepth)
        {
            if (!_callDepthReported)
            {
                _callDepthReported = true;
                _diagnostics.Error(
                    nameSpan,
                    $"'{function.Name}' is called {MaxCallDepth} calls deep; "
                    + "a function that calls itself needs a case that does not");
            }

            return false;
        }

        return true;
    }

    private static string Arguments(int count) =>
        count == 1 ? "1 argument" : $"{count} arguments";

    private void Define(
        string name,
        SdlValue value,
        SourceSpan where,
        Scope scope,
        bool isPrivate = false)
    {
        if (scope.Contains(name))
        {
            RejectRedefinition(name, where, scope);
            return;
        }

        scope.Define(name, value);

        if (isPrivate)
        {
            scope.MarkPrivate(name);
        }
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
        _diagnostics.Error(
            where,
            scope.IsBuiltin(name)
                ? $"'{name}' is a built-in of the language"
                : $"'{name}' is already defined");
    }

    /// <summary>
    /// Whether a name belongs to the built-in frame, reporting the write if it does.
    /// </summary>
    /// <remarks>
    /// Asked of the frame rather than of the value's type, so that <c>PI</c> — an ordinary
    /// number that happens to live there — is as unwritable as <c>random</c> is.
    /// </remarks>
    private bool RejectWriteToBuiltin(string name, SourceSpan where, Scope scope)
    {
        if (!scope.IsBuiltin(name))
        {
            return false;
        }

        _diagnostics.Error(where, $"'{name}' is a built-in of the language, and nothing assigns to one");
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
    /// Loads an imported file, running it in a frame that cannot see this one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The visibility rule is asymmetric, and each direction earns its keep. <b>Out:</b> the
    /// file's declarations are published, because a file of materials that exports nothing is
    /// not worth importing, and <c>private</c> is how it keeps a helper back. <b>In:</b> the
    /// file cannot see the importer's bindings, so it means the same thing wherever it is
    /// dropped and cannot be broken by a host scene that happens to define a name it uses.
    /// </para>
    /// <para>
    /// <b>The alias decides where the exports land, and nothing else.</b> Without one they join
    /// the importing scope, flat, which is what the keyword did when it was called
    /// <c>include</c>; with one they go into a <see cref="ModuleValue"/> and are reached
    /// through it, which is what lets two files both define <c>gold</c>. Either way the file
    /// <i>runs</i>, so a fragment that declares solids contributes them.
    /// </para>
    /// </remarks>
    private void ExecuteImport(
        ImportStatement statement,
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
            // The full path, not the path as written: a relative import is relative to the
            // file that wrote it, and printing it verbatim would name a location that does
            // not exist from wherever the renderer was run.
            SourceText source = new(path, text);
            IReadOnlyList<Token> tokens = Lexer.Tokenize(source, _diagnostics);
            SceneFile file = Parser.Parse(tokens, _diagnostics);

            Scope fragment = RootScope();
            Execute(file.Statements, fragment, entries);

            if (statement.Alias is { } alias)
            {
                Dictionary<string, SdlValue> exports =
                    fragment.Exports.ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);

                Define(
                    alias,
                    new ModuleValue(statement.Span, alias, statement.Path, exports),
                    statement.AliasSpan,
                    scope);

                return;
            }

            foreach ((string name, SdlValue value) in fragment.Exports)
            {
                if (scope.Contains(name))
                {
                    _diagnostics.Error(
                        statement.PathSpan,
                        $"'{statement.Path}' defines '{name}', which is already defined here; "
                        + $"write 'import \"{statement.Path}\" as …' to reach it by name instead");

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
    /// Relative to the importing file rather than to the working directory, so a folder of
    /// fragments that include each other keeps working wherever the renderer is run from.
    /// The scene file named on the command line is the one exception the language already
    /// had, and it stays resolved against the working directory.
    /// </remarks>
    private (string Path, string? Text) Resolve(ImportStatement statement)
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
                $"'{statement.Path}' is already being imported; imports may not form a cycle");

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


    /// <summary>
    /// <c>[a, b, c]</c>, whose elements may be values of any kind.
    /// </summary>
    /// <remarks>
    /// Elements used to have to be numbers, and the check that enforced it is gone rather than
    /// widened: an array of arrays is a list of points, an array of structs is a list of
    /// records, and an array of node blocks is a list of solids waiting to be placed. An array
    /// whose elements happen to all be numbers is the vector that was there before, and every
    /// field that wants one still says so where it reads it.
    /// </remarks>
    private SdlValue? EvaluateArray(ArrayExpression expression, Scope scope)
    {
        List<SdlValue> elements = new(expression.Elements.Count);

        foreach (Expression element in expression.Elements)
        {
            SdlValue? value = Evaluate(element, scope);
            if (value is null)
            {
                return null;
            }

            elements.Add(value);
        }

        return new ArrayValue(expression.Span, elements);
    }

    /// <summary>
    /// <c>a[i]</c>, on an array only.
    /// </summary>
    /// <remarks>
    /// The index is a whole number in range, and each of those is reported rather than guessed
    /// at: rounding a fraction would answer a question the file did not ask, and this language
    /// has no negative-from-the-end convention to fall back on.
    /// </remarks>
    private SdlValue? EvaluateIndex(IndexExpression expression, Scope scope)
    {
        SdlValue? target = Evaluate(expression.Target, scope);
        if (target is null)
        {
            return null;
        }

        if (target is not ArrayValue array)
        {
            return Reject(expression.Target.Span, $"cannot index {target.Describe()}");
        }

        return ResolveIndex(array, expression, scope) is { } index ? array.Elements[index] : null;
    }

    /// <summary>
    /// The element an index expression selects, or null having reported why it selects none.
    /// </summary>
    /// <remarks>
    /// Shared by reading an element and by assigning to one, so the two agree on what a usable
    /// index is and say the same thing when given something else.
    /// </remarks>
    private int? ResolveIndex(ArrayValue array, IndexExpression expression, Scope scope)
    {
        if (Evaluate(expression.Index, scope) is not { } index)
        {
            return null;
        }

        if (index is not NumberValue number)
        {
            Reject(expression.Index.Span, $"an index must be a number, found {index.Describe()}");
            return null;
        }

        if (number.Value != Math.Floor(number.Value))
        {
            Reject(
                expression.Index.Span,
                $"an index must be a whole number, found {Printed(number.Value)}");

            return null;
        }

        if (number.Value < 0 || number.Value >= array.Count)
        {
            Reject(
                expression.Index.Span,
                array.Count == 0
                    ? $"index {Printed(number.Value)} is out of range; the array is empty"
                    : $"index {Printed(number.Value)} is out of range; the array has "
                      + $"{array.Count} element{(array.Count == 1 ? "" : "s")}, "
                      + $"so 0 to {array.Count - 1}");

            return null;
        }

        return (int)number.Value;
    }

    /// <summary>
    /// <c>a.name</c>: a field of a struct, or <c>length</c> on an array.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>length</c> is the one member the language supplies, and it is a member rather than a
    /// built-in function for two reasons: <c>a.length</c> is the spelling a reader arrives
    /// with, and <c>length</c> as a name is already spoken for by the vector magnitude the
    /// maths entry wants. A struct with a field called <c>length</c> is unaffected — the kinds
    /// are different and each is read by its own branch.
    /// </para>
    /// <para>
    /// <b>A node block has no fields to read.</b> That refusal is the whole distinction a
    /// <c>struct</c> declaration buys: <c>sphere { radius: 1 }</c> is a description a binder
    /// later reads, not a record, and letting it be probed by key would make every node's field
    /// set part of the language rather than part of its binder.
    /// </para>
    /// </remarks>
    private SdlValue? EvaluateMember(MemberExpression expression, Scope scope)
    {
        SdlValue? target = Evaluate(expression.Target, scope);
        if (target is null)
        {
            return null;
        }

        switch (target)
        {
            case ArrayValue array when expression.Name == "length":
                return new NumberValue(expression.Span, array.Count);

            case ArrayValue:
                return Reject(
                    expression.NameSpan,
                    $"an array has no '{expression.Name}'; 'length' is the only one it has");

            case ModuleValue module when module.Exports.TryGetValue(expression.Name, out SdlValue? export):
                return export;

            case ModuleValue module:
                return Reject(
                    expression.NameSpan,
                    $"'{module.Path}' does not export '{expression.Name}'"
                    + (module.Exports.Count == 0
                        ? "; it exports nothing"
                        : $"; it exports {Names(module.Exports.Keys)}"));

            case StructValue instance when instance.Fields.TryGetValue(expression.Name, out SdlValue? field):
                return field;

            case StructValue instance:
                return Reject(
                    expression.NameSpan,
                    $"'{instance.Type.Name}' has no field '{expression.Name}'; it has "
                    + Fields(instance.Type));

            case ObjectValue block:
                return Reject(
                    expression.NameSpan,
                    $"{block.Describe()} is a node rather than a record, and its fields are "
                    + "read by the binder rather than by the file; declare a 'struct' to read "
                    + "fields by name");

            default:
                return Reject(expression.NameSpan, $"{target.Describe()} has no fields");
        }
    }

    /// <summary>
    /// The declared field names of a struct type, in declaration order, for a diagnostic.
    /// </summary>
    private static string Fields(StructTypeValue type) =>
        type.Fields.Count == 0 ? "none" : string.Join(", ", type.Fields.Select(f => $"'{f}'"));

    /// <summary>
    /// A module's exports for a diagnostic: sorted, since a dictionary has no order worth
    /// showing, and cut off before the list stops being something a reader scans.
    /// </summary>
    private static string Names(IEnumerable<string> names)
    {
        string[] all = [.. names.Order(StringComparer.Ordinal)];

        return all.Length <= 6
            ? string.Join(", ", all.Select(n => $"'{n}'"))
            : string.Join(", ", all.Take(6).Select(n => $"'{n}'")) + $" and {all.Length - 6} more";
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

        if (operand is NumberValue number)
        {
            return new NumberValue(expression.Span, -number.Value);
        }

        // Component-wise, and only when every element is one. An array that nests has no
        // negation worth guessing at, and saying so names the array rather than an element.
        if (operand is ArrayValue array && array.AsNumbers() is { } numbers)
        {
            return new ArrayValue(
                expression.Span, [.. numbers.Select(c => (SdlValue)new NumberValue(expression.Span, -c))]);
        }

        return Reject(expression.Span, $"cannot negate {operand.Describe()}");
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
        bool? equal = Equal(left, right);

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
    /// Whether two values are equal, or null if the question is a mistake rather than a false.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Arrays compare element by element</b>, recursively, and two of different lengths are
    /// unequal rather than incomparable — they are the same kind, and a length is a fact about
    /// a value rather than about its type. An array holding two things that cannot be compared
    /// takes that answer back up: <c>[sphere { }] == [sphere { }]</c> is the mistake
    /// <c>sphere { } == sphere { }</c> is, and is reported the same way.
    /// </para>
    /// <para>
    /// <b>Structs compare field by field, and only against their own type.</b> Two types with
    /// the same field names are still two types, so comparing them is a mistake in the file
    /// exactly as comparing a number with a string is — which is the reading a record type is
    /// declared for.
    /// </para>
    /// </remarks>
    private static bool? Equal(SdlValue left, SdlValue right)
    {
        switch (left, right)
        {
            case (NumberValue a, NumberValue b):
                return a.Value == b.Value;

            case (StringValue a, StringValue b):
                return string.Equals(a.Value, b.Value, StringComparison.Ordinal);

            case (BooleanValue a, BooleanValue b):
                return a.Value == b.Value;

            case (ArrayValue a, ArrayValue b):
            {
                if (a.Count != b.Count)
                {
                    return false;
                }

                bool same = true;

                for (int i = 0; i < a.Count; i++)
                {
                    if (Equal(a.Elements[i], b.Elements[i]) is not { } element)
                    {
                        return null;
                    }

                    same &= element;
                }

                return same;
            }

            case (StructValue a, StructValue b):
            {
                if (!ReferenceEquals(a.Type, b.Type))
                {
                    return null;
                }

                bool same = true;

                foreach (string field in a.Type.Fields)
                {
                    if (Equal(a.Fields[field], b.Fields[field]) is not { } value)
                    {
                        return null;
                    }

                    same &= value;
                }

                return same;
            }

            default:
                return null;
        }
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
        // Only numbers and arrays of numbers have arithmetic. Naming which operand is at fault
        // matters more than it looks: `a + b` where one of them is a 'let' binding of the wrong
        // kind is otherwise a message about a line that reads perfectly well.
        foreach (SdlValue operand in new[] { left, right })
        {
            if (operand is NumberValue)
            {
                continue;
            }

            if (operand is ArrayValue array)
            {
                if (array.AsNumbers() is not null)
                {
                    continue;
                }

                // An array that nests is refused as a whole rather than element by element: a
                // list of points has no arithmetic, and the answer is not a deeper broadcast.
                return Reject(
                    operand.Span,
                    $"arithmetic needs an array of numbers, found {operand.Describe()}");
            }

            string what = operand switch
            {
                ObjectValue => "objects",
                BooleanValue => "booleans",
                FunctionValue or BuiltinValue => "functions",
                StructValue instance => $"'{instance.Type.Name}' structs",
                StructTypeValue => "struct types",
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

        SdlValue[] result = new SdlValue[count];
        for (int i = 0; i < count; i++)
        {
            double a = leftComponents.Count == 1 ? leftComponents[0] : leftComponents[i];
            double b = rightComponents.Count == 1 ? rightComponents[0] : rightComponents[i];
            result[i] = new NumberValue(expression.Span, apply(a, b));
        }

        return new ArrayValue(expression.Span, result);
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

    /// <summary>
    /// An operand's numbers, the caller having already established that it has some.
    /// </summary>
    private static IReadOnlyList<double> AsComponents(SdlValue value) => value switch
    {
        NumberValue number => [number.Value],
        ArrayValue array => array.AsNumbers() ?? [],
        _ => [],
    };

    private SdlValue? EvaluateObject(ObjectExpression expression, Scope scope)
    {
        // A block is a frame: a 'let' written inside one belongs to it, which is what keeps
        // a helper value next to the geometry that uses it.
        List<BoundEntry> entries = [];
        Execute(expression.Body, scope.Nested(), entries);

        // 'shapes.Post { … }': the type is reached through a module, so it can only be a
        // struct. A node name is not a binding and nothing exports one.
        if (expression.Target is not null)
        {
            return ResolveStructType(expression, scope) is { } qualified
                ? BuildStruct(expression, qualified, entries)
                : null;
        }

        // 'Point { x: 1 }' and 'sphere { radius: 1 }' are the same syntax, and which one a
        // block is comes from what its name resolves to. A struct type in scope wins; anything
        // else is a node, whose name is looked up by a binder much later and is deliberately
        // not a binding at all.
        if (expression.TypeName is { } typeName
            && scope.TryGet(typeName, out SdlValue named)
            && named is StructTypeValue type)
        {
            return BuildStruct(expression, type, entries);
        }

        return new ObjectValue(
            expression.Span,
            expression.TypeName,
            expression.TypeNameSpan,
            entries);
    }

    /// <summary>
    /// The struct type a qualified <c>module.Type { … }</c> names.
    /// </summary>
    private StructTypeValue? ResolveStructType(ObjectExpression expression, Scope scope)
    {
        if (Evaluate(expression.Target!, scope) is not { } holder)
        {
            return null;
        }

        if (holder is not ModuleValue module)
        {
            _diagnostics.Error(
                expression.Target!.Span,
                $"{holder.Describe()} is not a module, so '{expression.TypeName}' cannot name "
                + "a type through it");

            return null;
        }

        if (!module.Exports.TryGetValue(expression.TypeName!, out SdlValue? exported))
        {
            _diagnostics.Error(
                expression.TypeNameSpan,
                $"'{module.Path}' does not export '{expression.TypeName}'"
                + (module.Exports.Count == 0
                    ? "; it exports nothing"
                    : $"; it exports {Names(module.Exports.Keys)}"));

            return null;
        }

        if (exported is StructTypeValue type)
        {
            return type;
        }

        // Node types are the other thing a block can be, and they are not exported by anything:
        // a node name is looked up by a binder rather than bound, so 'shapes.sphere { }' names
        // nothing however the module is written.
        _diagnostics.Error(
            expression.TypeNameSpan,
            $"'{expression.TypeName}' is {exported.Describe()} rather than a struct type, "
            + "so it cannot open a block");

        return null;
    }

    /// <summary>
    /// Builds one instance of a struct type from the entries its block produced.
    /// </summary>
    /// <remarks>
    /// Every field is required and no other is accepted, which is the whole difference between
    /// a record type and an object literal that happens to have the right keys — and it is
    /// what lets a mistake be reported here, at the instance, rather than wherever the missing
    /// value was eventually needed. Order does not matter; the declaration fixes it.
    /// </remarks>
    private SdlValue? BuildStruct(
        ObjectExpression expression,
        StructTypeValue type,
        IReadOnlyList<BoundEntry> entries)
    {
        Dictionary<string, SdlValue> fields = new(StringComparer.Ordinal);
        bool ok = true;

        foreach (BoundEntry entry in entries)
        {
            if (entry is not BoundField field)
            {
                _diagnostics.Error(
                    entry.Span,
                    $"'{type.Name}' is a struct and takes only its fields, "
                    + "not child objects");

                ok = false;
                continue;
            }

            if (!type.Fields.Contains(field.Name, StringComparer.Ordinal))
            {
                _diagnostics.Error(
                    field.NameSpan,
                    $"'{type.Name}' has no field '{field.Name}'; it has {Fields(type)}");

                ok = false;
                continue;
            }

            if (!fields.TryAdd(field.Name, field.Value))
            {
                _diagnostics.Error(
                    field.NameSpan,
                    $"field '{field.Name}' is set more than once on '{type.Name}'");

                ok = false;
            }
        }

        // Listed together rather than one diagnostic each: a struct written from memory tends
        // to be missing several, and three messages about one block is two too many.
        string[] missing = [.. type.Fields.Where(f => !fields.ContainsKey(f))];

        if (missing.Length > 0)
        {
            _diagnostics.Error(
                expression.TypeNameSpan,
                $"'{type.Name}' is missing {(missing.Length == 1 ? "field" : "fields")} "
                + string.Join(", ", missing.Select(f => $"'{f}'")));

            ok = false;
        }

        return ok ? new StructValue(expression.Span, type, fields) : null;
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
