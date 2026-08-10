using System.Numerics;

namespace Chroma.Core.Model.Geometry.Primitives;

/// <summary>
/// A ring lying in the XZ plane with the Y axis through its hole, as POV-Ray's is.
/// </summary>
/// <remarks>
/// The first primitive here whose surface is quartic rather than quadric, and the first
/// that is not convex: a ray through the hole and back out crosses it twice, so a torus
/// leaf can produce two spans.
/// </remarks>
public sealed class Torus : Solid
{
    public Vector3 Center { get; init; } = Vector3.Zero;

    /// <summary>Centre of the hole to the mid-line of the rim.</summary>
    public float MajorRadius { get; init; } = 1f;

    /// <summary>Radius of the rim's circular cross-section.</summary>
    public float MinorRadius { get; init; } = 0.25f;

    public override string Kind => "Torus";

    public override void Accept(ISolidVisitor visitor) => visitor.VisitTorus(this);

    public override TResult Accept<TResult>(ISolidVisitor<TResult> visitor) =>
        visitor.VisitTorus(this);
}
