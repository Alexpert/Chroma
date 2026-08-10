using ChromaTest.Core.Sdl.Source;
using ChromaTest.Core.Sdl.Syntax;

namespace ChromaTest.Core.Sdl.Binding;

/// <summary>
/// Folds a syntax expression into an <see cref="SdlValue"/>: resolves names against the
/// scope and evaluates arithmetic. Returns null when the expression could not be
/// evaluated, having already reported why.
/// </summary>
public sealed class Evaluator(Scope scope, DiagnosticBag diagnostics)
{
    private readonly Scope _scope = scope;
    private readonly DiagnosticBag _diagnostics = diagnostics;

    public SdlValue? Evaluate(Expression expression) => expression switch
    {
        NumberExpression number => new NumberValue(number.Span, number.Value),
        StringExpression text => new StringValue(text.Span, text.Value),
        VectorExpression vector => EvaluateVector(vector),
        IdentifierExpression identifier => EvaluateIdentifier(identifier),
        UnaryExpression unary => EvaluateUnary(unary),
        BinaryExpression binary => EvaluateBinary(binary),
        ObjectExpression obj => EvaluateObject(obj),

        // The parser already reported this one; saying so twice helps nobody.
        MissingExpression => null,

        _ => null,
    };

    private SdlValue? EvaluateVector(VectorExpression expression)
    {
        List<double> components = new(expression.Components.Count);

        foreach (Expression component in expression.Components)
        {
            SdlValue? value = Evaluate(component);
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

    private SdlValue? EvaluateIdentifier(IdentifierExpression expression)
    {
        if (_scope.TryGet(expression.Name, out SdlValue value))
        {
            return value;
        }

        _diagnostics.Error(expression.Span, $"unknown name '{expression.Name}'");
        return null;
    }

    private SdlValue? EvaluateUnary(UnaryExpression expression)
    {
        SdlValue? operand = Evaluate(expression.Operand);

        switch (operand)
        {
            case null:
                return null;

            case NumberValue number:
                return new NumberValue(expression.Span, -number.Value);

            case VectorValue vector:
                return new VectorValue(expression.Span, [.. vector.Components.Select(c => -c)]);

            default:
                _diagnostics.Error(
                    expression.Span,
                    $"cannot negate {operand.Describe()}");
                return null;
        }
    }

    private SdlValue? EvaluateBinary(BinaryExpression expression)
    {
        SdlValue? left = Evaluate(expression.Left);
        SdlValue? right = Evaluate(expression.Right);

        if (left is null || right is null)
        {
            return null;
        }

        // Only numbers and vectors have arithmetic. Naming which operand is at fault matters
        // more than it looks: `a + b` where one of them is a 'let' binding of the wrong kind
        // is otherwise a message about a line that reads perfectly well.
        foreach (SdlValue operand in new[] { left, right })
        {
            if (operand is NumberValue or VectorValue)
            {
                continue;
            }

            string what = operand is ObjectValue ? "objects" : "strings";
            _diagnostics.Error(operand.Span, $"{what} do not support arithmetic");
            return null;
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
            _diagnostics.Error(
                expression.Span,
                $"cannot combine {left.Describe()} with {right.Describe()}");
            return null;
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

    private static IReadOnlyList<double> AsComponents(SdlValue value) => value switch
    {
        NumberValue number => [number.Value],
        VectorValue vector => vector.Components,
        _ => [],
    };

    private SdlValue? EvaluateObject(ObjectExpression expression)
    {
        List<BoundEntry> entries = new(expression.Entries.Count);

        foreach (BlockEntry entry in expression.Entries)
        {
            switch (entry)
            {
                case FieldEntry field:
                {
                    SdlValue? value = Evaluate(field.Value);
                    if (value is not null)
                    {
                        entries.Add(new BoundField(field.Span, field.Name, field.NameSpan, value));
                    }

                    break;
                }

                case ChildEntry child:
                {
                    SdlValue? value = Evaluate(child.Value);
                    if (value is not null)
                    {
                        entries.Add(new BoundChild(child.Span, value));
                    }

                    break;
                }
            }
        }

        return new ObjectValue(
            expression.Span,
            expression.TypeName,
            expression.TypeNameSpan,
            entries);
    }
}
