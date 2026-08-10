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
