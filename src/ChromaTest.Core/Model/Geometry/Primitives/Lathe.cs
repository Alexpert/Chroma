using System.Numerics;

namespace ChromaTest.Core.Model.Geometry.Primitives;

/// <summary>
/// A closed outline in the half-plane <c>(radius, y)</c>, revolved about the Y axis.
/// </summary>
/// <remarks>
/// <para>
/// POV-Ray's <c>lathe</c>, restricted to a single contour. The outline is closed implicitly,
/// and no point may have a negative radius: the surface of revolution of a curve that crosses
/// the axis is not a solid.
/// </para>
/// <para>
/// <see cref="Points"/> is always the <b>tessellated</b> outline. A cubic Bézier spline is
/// flattened into segments by the binder, so nothing past this point — not the compiler, not
/// the shader — knows the curve existed. That is what keeps a curved lathe free on the GPU
/// side: it is the same polyline machinery with more vertices.
/// </para>
/// </remarks>
public sealed class Lathe : Solid
{
    /// <summary>
    /// Outline vertices as <c>(radius, y)</c>, in order, without repeating the first, after
    /// any spline has been flattened.
    /// </summary>
    public required IReadOnlyList<Vector2> Points { get; init; }

    /// <summary>
    /// Whether the outline came from a curve, and so whether its normals should be blended
    /// across segment joints instead of stepping at each one.
    /// </summary>
    /// <remarks>
    /// This is the difference between a Bézier lathe that reads as a curve and one that reads
    /// as a stack of rings: flattening fixes the silhouette, which is smooth at any step
    /// count, but the shading facets stay visible however fine the tessellation. It is a
    /// property of what the file meant, not of the polyline — a hand-written outline wants its
    /// corners to stay corners.
    /// </remarks>
    public bool Smooth { get; init; }

    public override string Kind => "Lathe";

    public override void Accept(ISolidVisitor visitor) => visitor.VisitLathe(this);

    public override TResult Accept<TResult>(ISolidVisitor<TResult> visitor) =>
        visitor.VisitLathe(this);
}
