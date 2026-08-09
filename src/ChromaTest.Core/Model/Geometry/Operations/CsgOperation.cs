namespace ChromaTest.Core.Model.Geometry.Operations;

/// <summary>
/// A boolean combination of solids. Operands are kept n-ary as the author wrote them;
/// binarising into a chain of two-operand nodes is the GPU compiler's job, not the
/// model's.
/// </summary>
public abstract class CsgOperation : Solid
{
    public required IReadOnlyList<Solid> Operands { get; init; }
}

/// <summary>Everything inside any operand. Order-independent.</summary>
public sealed class Union : CsgOperation
{
    public override string Kind => "Union";

    public override void Accept(ISolidVisitor visitor) => visitor.VisitUnion(this);

    public override TResult Accept<TResult>(ISolidVisitor<TResult> visitor) =>
        visitor.VisitUnion(this);
}

/// <summary>Only what is inside every operand. Order-independent.</summary>
public sealed class Intersection : CsgOperation
{
    public override string Kind => "Intersection";

    public override void Accept(ISolidVisitor visitor) => visitor.VisitIntersection(this);

    public override TResult Accept<TResult>(ISolidVisitor<TResult> visitor) =>
        visitor.VisitIntersection(this);
}

/// <summary>The first operand minus all the others. Order matters.</summary>
public sealed class Difference : CsgOperation
{
    public override string Kind => "Difference";

    public override void Accept(ISolidVisitor visitor) => visitor.VisitDifference(this);

    public override TResult Accept<TResult>(ISolidVisitor<TResult> visitor) =>
        visitor.VisitDifference(this);
}
