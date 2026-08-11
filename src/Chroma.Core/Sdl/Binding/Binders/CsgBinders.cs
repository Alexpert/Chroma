using Chroma.Core.Model.Geometry;
using Chroma.Core.Model.Geometry.Operations;

namespace Chroma.Core.Sdl.Binding.Binders;

/// <summary>
/// Base for the boolean operators. They differ only in their name, their minimum operand
/// count and the node they build.
/// </summary>
public abstract class CsgBinder : SolidBinder
{
    protected abstract int MinimumOperands { get; }

    protected abstract CsgOperation Create(IReadOnlyList<Solid> operands);

    protected override Solid? BindShape(BlockReader reader, BindingContext context)
    {
        IReadOnlyList<SdlValue> children = reader.Children();

        // Arity is checked against what was written, not against what bound successfully:
        // an operand that failed to bind has already produced its own diagnostic, and
        // saying "needs at least 2 operands" on top of it would be misleading.
        if (children.Count < MinimumOperands)
        {
            reader.Diagnostics.Error(
                reader.NameSpan,
                $"'{Name}' needs at least {MinimumOperands} operand{(MinimumOperands == 1 ? "" : "s")}, "
                + $"found {children.Count}");
            return null;
        }

        List<Solid> operands = new(children.Count);

        foreach (SdlValue child in children)
        {
            if (context.BindSolid(child) is { } solid)
            {
                operands.Add(solid);
            }
        }

        return operands.Count == children.Count ? Create(operands) : null;
    }
}

/// <summary>
/// <c>object { solid, translate: …, material: … }</c> — one solid, wrapped so that the
/// shared modifiers can be hung on it.
/// </summary>
/// <remarks>
/// <para>
/// It exists because a reference on its own takes no modifiers: <c>unit { translate: … }</c>
/// would read as a node type called <c>unit</c>, so placing a copy of a <c>let</c> binding
/// meant writing <c>union { unit, translate: … }</c> — a boolean operator with nothing to
/// combine, named after an operation it is not performing.
/// </para>
/// <para>
/// A union of one operand <i>is</i> that operand, so this is what it builds and the
/// compilation path is untouched: <c>EmitOperation</c> emits no operator instruction for a
/// single operand, and the span budget it returns is the operand's own. What the node adds
/// is the name, and an arity that says so — several solids are a <c>union</c>, and writing
/// that is the reader's cue that they merge.
/// </para>
/// </remarks>
public sealed class ObjectBinder : SolidBinder
{
    public override string Name => "object";

    protected override Solid? BindShape(BlockReader reader, BindingContext context)
    {
        IReadOnlyList<SdlValue> children = reader.Children();

        if (children.Count != 1)
        {
            reader.Diagnostics.Error(
                reader.NameSpan,
                $"'object' wraps exactly one solid, found {children.Count}"
                + (children.Count > 1 ? "; use 'union' to combine several" : string.Empty));

            return null;
        }

        return context.BindSolid(children[0]) is { } solid
            ? new Union { Operands = [solid] }
            : null;
    }
}

public sealed class UnionBinder : CsgBinder
{
    public override string Name => "union";

    protected override int MinimumOperands => 1;

    protected override CsgOperation Create(IReadOnlyList<Solid> operands) =>
        new Union { Operands = operands };
}

public sealed class IntersectionBinder : CsgBinder
{
    public override string Name => "intersection";

    protected override int MinimumOperands => 2;

    protected override CsgOperation Create(IReadOnlyList<Solid> operands) =>
        new Intersection { Operands = operands };
}

public sealed class DifferenceBinder : CsgBinder
{
    public override string Name => "difference";

    protected override int MinimumOperands => 2;

    protected override CsgOperation Create(IReadOnlyList<Solid> operands) =>
        new Difference { Operands = operands };
}
