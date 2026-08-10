using System.Numerics;

namespace Chroma.Core.Model.Geometry.Primitives;

/// <summary>
/// A truncated cone between two end points, capped at both ends. A zero radius at one end
/// gives the familiar pointed cone; equal radii give a cylinder.
/// </summary>
/// <remarks>
/// POV-Ray's <c>cone { &lt;base&gt;, r1, &lt;cap&gt;, r2 }</c>, minus the <c>open</c>
/// modifier: an uncapped cone has no well-defined inside, and CSG needs one.
/// </remarks>
public sealed class Cone : Solid
{
    public Vector3 Base { get; init; } = Vector3.Zero;

    public float BaseRadius { get; init; } = 1f;

    public Vector3 Cap { get; init; } = Vector3.UnitY;

    public float CapRadius { get; init; }

    public override string Kind => "Cone";

    public override void Accept(ISolidVisitor visitor) => visitor.VisitCone(this);

    public override TResult Accept<TResult>(ISolidVisitor<TResult> visitor) =>
        visitor.VisitCone(this);
}
