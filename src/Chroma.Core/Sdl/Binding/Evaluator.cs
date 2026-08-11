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
public sealed class Evaluator(DiagnosticBag diagnostics)
{
    /// <summary>
    /// Total loop-body executions allowed per load.
    /// </summary>
    /// <remarks>
    /// <c>for</c> is bounded by construction, which rules out a loop that never ends but not
    /// one that ends in an hour: <c>for (i in 0..1000000000)</c> parses, and a loader that
    /// disappears produces no diagnostic, no window and no exit code. A budget makes the
    /// failure reportable, and the number is chosen to be far above any scene worth writing
    /// by hand — the lattice deliverable spends 125 of it.
    /// </remarks>
    public const int MaxLoopIterations = 100_000;

    /// <summary>
    /// Total function calls allowed per load.
    /// </summary>
    /// <remarks>
    /// The same argument as <see cref="MaxLoopIterations"/>, for the same failure. A function
    /// body can see the function's own name, so recursion works — and a recursion that
    /// branches costs exponentially many calls at a depth
    /// <see cref="MaxCallDepth"/> allows, which the depth limit alone would not catch.
    /// </remarks>
    public const int MaxFunctionCalls = 100_000;

    /// <summary>
    /// How deeply calls may nest.
    /// </summary>
    /// <remarks>
    /// A recursion that never reaches its base case is stopped here rather than by
    /// <see cref="MaxFunctionCalls"/>, because the evaluator recurses on the CLR stack and a
    /// <see cref="StackOverflowException"/> cannot be reported: it takes the process down
    /// with no diagnostic at all, which is the one outcome the loader must never have.
    /// </remarks>
    public const int MaxCallDepth = 64;

    private readonly DiagnosticBag _diagnostics = diagnostics;

    // Full paths of the files currently open, innermost last, seeded with the scene file so
    // that a file including itself is caught on the first attempt rather than the second.
    private readonly List<string> _includeStack = [FullPathOrEmpty(diagnostics.Source.Path)];

    private int _iterationsLeft = MaxLoopIterations;
    private bool _loopBudgetReported;

    private int _callsLeft = MaxFunctionCalls;
    private int _callDepth;
    private bool _callBudgetReported;
    private bool _callDepthReported;

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
            switch (statement)
            {
                case LetStatement let:
                    ExecuteLet(let, scope);
                    break;

                case FunctionStatement function:
                    ExecuteFunction(function, scope);
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

    /// <summary>
    /// Binds a <c>fn</c> declaration to its name. Nothing is evaluated until it is called.
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
                _diagnostics.Error(parameter.Span, $"'{parameter.Name}' is already defined");
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
            SdlValue? result = Evaluate(function.Body, frame);

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
    /// Charges one call against the two budgets, or reports which of them is exhausted.
    /// </summary>
    /// <remarks>
    /// Either failure is reported once and then stays silent, as the loop budget's is. A
    /// runaway recursion is one mistake however many calls meet the limit, and a recursion
    /// that branches would otherwise report it thousands of times over.
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

        if (_callsLeft == 0)
        {
            if (!_callBudgetReported)
            {
                _callBudgetReported = true;
                _diagnostics.Error(
                    expression.NameSpan,
                    $"a scene file may make {MaxFunctionCalls} function calls in total");
            }

            return false;
        }

        _callsLeft--;
        return true;
    }

    private static string Arguments(int count) =>
        count == 1 ? "1 argument" : $"{count} arguments";

    private void Define(string name, SdlValue value, SourceSpan where, Scope scope)
    {
        if (scope.Contains(name))
        {
            _diagnostics.Error(where, $"'{name}' is already defined");
            return;
        }

        scope.Define(name, value);
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

    private void ExecuteFor(ForStatement statement, Scope scope, List<BoundEntry> entries)
    {
        // The loop variable is an ordinary binding and obeys the ordinary rule: it may not
        // shadow a name already in scope. Checked once here rather than per iteration, so a
        // collision is reported once rather than a hundred times.
        if (scope.Contains(statement.Variable))
        {
            _diagnostics.Error(statement.VariableSpan, $"'{statement.Variable}' is already defined");
            return;
        }

        if (Bound(statement.From, scope, "the start of the range") is not { } from
            || Bound(statement.To, scope, "the end of the range") is not { } to)
        {
            return;
        }

        // A range that runs backwards is empty rather than an error. 'for (i in 0..n)' with
        // n = 0 is the ordinary way to write "none of these", and refusing it would make
        // every generated count need a guard around it.
        int count = Math.Max(0, to - from);

        if (count > _iterationsLeft)
        {
            if (!_loopBudgetReported)
            {
                _loopBudgetReported = true;
                _diagnostics.Error(
                    statement.KeywordSpan,
                    $"this loop over '{statement.Variable}' runs {count} times; "
                    + $"a scene file may run {MaxLoopIterations} loop iterations in total");
            }

            return;
        }

        _iterationsLeft -= count;
        LoopOrigin origin = new(statement.KeywordSpan, statement.Variable, count);

        for (int i = 0; i < count; i++)
        {
            // A frame per iteration, so a 'let' in the body is fresh each time round rather
            // than colliding with itself on the second pass.
            Scope frame = scope.Nested();
            frame.Define(statement.Variable, new NumberValue(statement.VariableSpan, from + i));

            int before = entries.Count;
            Execute(statement.Body, frame, entries);
            TagGenerated(entries, before, origin);
        }
    }

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

            Scope fragment = new();
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

    /// <summary>One end of a <c>for</c> range: a whole number, or null having reported why.</summary>
    private int? Bound(Expression expression, Scope scope, string what)
    {
        SdlValue? value = Evaluate(expression, scope);

        if (value is null)
        {
            return null;
        }

        if (value is not NumberValue number)
        {
            _diagnostics.Error(value.Span, $"{what} must be a number, found {value.Describe()}");
            return null;
        }

        if (number.Value != Math.Floor(number.Value)
            || number.Value < int.MinValue
            || number.Value > int.MaxValue)
        {
            string printed = number.Value.ToString("0.###", CultureInfo.InvariantCulture);
            _diagnostics.Error(value.Span, $"{what} must be a whole number, found {printed}");
            return null;
        }

        return (int)number.Value;
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
                FunctionValue => "functions",
                _ => "strings",
            };

            return Reject(operand.Span, $"{what} do not support arithmetic");
        }

        Func<double, double, double> apply = expression.Operator switch
        {
            BinaryOperator.Add => static (a, b) => a + b,
            BinaryOperator.Subtract => static (a, b) => a - b,
            BinaryOperator.Multiply => static (a, b) => a * b,
            _ => static (a, b) => a / b,
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

    private static string Symbol(BinaryOperator op) => op switch
    {
        BinaryOperator.Less => "<",
        BinaryOperator.LessOrEqual => "<=",
        BinaryOperator.Greater => ">",
        _ => ">=",
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
