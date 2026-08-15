using Chroma.Core.Sdl.Syntax;

namespace Chroma.Core.Sdl.Binding;

/// <summary>
/// Finds <c>render { seed: … }</c> in a parsed file, before anything is evaluated.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists at all.</b> The seed belongs in <c>render { }</c> beside
/// <c>maxBounces</c>, because it is a property of the scene rather than of the build. But
/// <c>random</c> is drawn while the scene is being built, and the <c>render</c> block is bound
/// after the whole file has run — so by the time the ordinary path knows the seed, every
/// number that needed it has already been drawn. This reads the seed out of the <i>text</i>
/// first, which is the only place it can be read early enough.
/// </para>
/// <para>
/// <b>So the seed is written, not computed.</b> A plain number, or a minus sign and a plain
/// number, and nothing else: <c>seed: 6 + 1</c> is refused rather than silently answered,
/// because answering would mean evaluating an expression before the evaluator exists.
/// <see cref="SceneBuilder"/> checks the seed the <c>render</c> block finally bound against
/// the one this returned and reports the difference, which is what catches both that and a
/// <c>render</c> block written in an included fragment — a file this pass never sees.
/// </para>
/// <para>
/// The walk covers every statement list and every block in the file, not only the top level,
/// because a <c>render</c> block may be produced by an <c>if</c>, returned from a function or
/// written inside a loop. The first seed found wins; a file with two <c>render</c> blocks is
/// already an error, reported where it can be reported properly.
/// </para>
/// </remarks>
public static class SeedReader
{
    /// <summary>The seed the file writes, or null if it writes none this pass can read.</summary>
    public static int? Read(SceneFile file) => InStatements(file.Statements);

    private static int? InStatements(IReadOnlyList<Statement> statements)
    {
        foreach (Statement statement in statements)
        {
            if (InStatement(statement) is { } seed)
            {
                return seed;
            }
        }

        return null;
    }

    private static int? InStatement(Statement statement) => statement switch
    {
        LetStatement let => InExpression(let.Value),
        FieldStatement field => InExpression(field.Value),
        ExpressionStatement expression => InExpression(expression.Value),
        AssignmentStatement assignment => InExpression(assignment.Value),
        ReturnStatement returned => InExpression(returned.Value),
        FunctionStatement function => InStatements(function.Body),
        IfStatement conditional =>
            InExpression(conditional.Condition)
            ?? InStatements(conditional.Then)
            ?? InStatements(conditional.Else),
        ForStatement loop =>
            (loop.Init is null ? null : InStatement(loop.Init))
            ?? (loop.Condition is null ? null : InExpression(loop.Condition))
            ?? (loop.Step is null ? null : InStatement(loop.Step))
            ?? InStatements(loop.Body),
        _ => null,
    };

    private static int? InExpression(Expression expression)
    {
        switch (expression)
        {
            case ObjectExpression block:
                return block.TypeName == "render" ? InRenderBlock(block) : InStatements(block.Body);

            case UnaryExpression unary:
                return InExpression(unary.Operand);

            case BinaryExpression binary:
                return InExpression(binary.Left) ?? InExpression(binary.Right);

            case ConditionalExpression conditional:
                return InExpression(conditional.Condition)
                    ?? InExpression(conditional.WhenTrue)
                    ?? InExpression(conditional.WhenFalse);

            case CallExpression call:
                foreach (Expression argument in call.Arguments)
                {
                    if (InExpression(argument) is { } seed)
                    {
                        return seed;
                    }
                }

                return null;

            case VectorExpression vector:
                foreach (Expression component in vector.Components)
                {
                    if (InExpression(component) is { } seed)
                    {
                        return seed;
                    }
                }

                return null;

            default:
                return null;
        }
    }

    /// <summary>
    /// The <c>seed</c> field of a <c>render</c> block, or null if it has none this can read.
    /// </summary>
    /// <remarks>
    /// A nested <c>render</c> block is still searched afterwards, because refusing to look is
    /// not the same as refusing the file: whichever block finally binds is the one the check
    /// in <see cref="SceneBuilder"/> compares against.
    /// </remarks>
    private static int? InRenderBlock(ObjectExpression block)
    {
        foreach (Statement statement in block.Body)
        {
            if (statement is FieldStatement { Name: "seed" } field && Literal(field.Value) is { } seed)
            {
                return seed;
            }
        }

        return InStatements(block.Body);
    }

    /// <summary>A written whole number, negated or not, within the range of a seed.</summary>
    private static int? Literal(Expression expression)
    {
        double sign = 1.0;

        if (expression is UnaryExpression { Operator: UnaryOperator.Negate } negated)
        {
            sign = -1.0;
            expression = negated.Operand;
        }

        if (expression is not NumberExpression number)
        {
            return null;
        }

        double value = sign * number.Value;

        // Out of range or fractional means the binder is about to report it and fall back to
        // the default, which is exactly what returning null here makes the evaluator use.
        return value == Math.Floor(value) && value >= int.MinValue && value <= int.MaxValue
            ? (int)value
            : null;
    }
}
