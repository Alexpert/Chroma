using System.Numerics;

namespace Chroma.Core.Model.Geometry.Primitives;

/// <summary>
/// One or more closed outlines in the half-plane <c>(radius, y)</c>, revolved about the Y axis.
/// </summary>
/// <remarks>
/// <para>
/// POV-Ray's <c>lathe</c>. Each outline is closed implicitly, and no point may have a negative
/// radius: the surface of revolution of a curve that crosses the axis is not a solid. Several
/// outlines combine by the even-odd rule, which is how a hollow vase is written as its own wall
/// rather than as a <c>difference</c>.
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
    private readonly IReadOnlyList<int>? _contourSizes;

    /// <summary>
    /// Outline vertices as <c>(radius, y)</c>, in order, without repeating the first of any
    /// contour, after any spline has been flattened. With more than one contour this is their
    /// concatenation, split by <see cref="ContourSizes"/>.
    /// </summary>
    public required IReadOnlyList<Vector2> Points { get; init; }

    /// <summary>
    /// How many of <see cref="Points"/> belong to each contour, in order, summing to its count.
    /// Defaults to one contour holding all of them.
    /// </summary>
    public IReadOnlyList<int> ContourSizes
    {
        get => _contourSizes ?? [Points.Count];
        init => _contourSizes = value;
    }

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
