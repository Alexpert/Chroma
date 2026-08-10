using System.Numerics;

namespace ChromaTest.Core.Model.Geometry.Primitives;

/// <summary>
/// A closed outline in the half-plane <c>(radius, y)</c>, revolved about the Y axis.
/// </summary>
/// <remarks>
/// POV-Ray's <c>lathe</c>, restricted to a linear spline and to a single contour — the same
/// restriction, and for the same reason, as <see cref="Prism"/>. The outline is closed
/// implicitly, and no point may have a negative radius: the surface of revolution of a
/// curve that crosses the axis is not a solid.
/// </remarks>
public sealed class Lathe : Solid
{
    /// <summary>
    /// Outline vertices as <c>(radius, y)</c>, in order, without repeating the first.
    /// </summary>
    public required IReadOnlyList<Vector2> Points { get; init; }

    public override string Kind => "Lathe";

    public override void Accept(ISolidVisitor visitor) => visitor.VisitLathe(this);

    public override TResult Accept<TResult>(ISolidVisitor<TResult> visitor) =>
        visitor.VisitLathe(this);
}
