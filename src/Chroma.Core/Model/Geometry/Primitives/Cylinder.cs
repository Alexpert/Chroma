using System.Numerics;

namespace Chroma.Core.Model.Geometry.Primitives;

/// <summary>
/// A cylinder between two end points. Capped at both ends — it is a solid, not a tube,
/// because CSG needs a well-defined inside.
/// </summary>
public sealed class Cylinder : Solid
{
    public Vector3 Base { get; init; } = Vector3.Zero;

    public Vector3 Cap { get; init; } = Vector3.UnitY;

    public float Radius { get; init; } = 1f;

    public override string Kind => "Cylinder";

    public override void Accept(ISolidVisitor visitor) => visitor.VisitCylinder(this);

    public override TResult Accept<TResult>(ISolidVisitor<TResult> visitor) =>
        visitor.VisitCylinder(this);
}
