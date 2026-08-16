using System.Numerics;

namespace Chroma.Core.Model.Geometry.Primitives;

/// <summary>
/// One or more closed polygons in the XZ plane, swept along Y between two heights and capped.
/// </summary>
/// <remarks>
/// <para>
/// POV-Ray's <c>prism</c>. A cubic Bézier outline is flattened into segments by the binder,
/// exactly as a <see cref="Lathe"/>'s is and by the same code, so nothing past this point knows
/// the curve existed.
/// </para>
/// <para>
/// Each contour is closed implicitly: its last point joins back to its own first. Several of
/// them combine by the even-odd rule, so a contour drawn inside another punches a hole. That
/// falls out of the tracing rather than being arranged — the span code sorts the ray's
/// crossings of every wall and pairs them, and pairing sorted crossings <i>is</i> the even-odd
/// rule — so the only thing several contours cost is knowing where each one ends.
/// </para>
/// </remarks>
public sealed class Prism : Solid
{
    private readonly IReadOnlyList<int>? _contourSizes;

    public float Bottom { get; init; }

    public float Top { get; init; } = 1f;

    /// <summary>
    /// Vertices in the XZ plane, in order, without repeating the first of any contour. With
    /// more than one contour this is their concatenation, split by <see cref="ContourSizes"/>.
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
    /// across segment joints instead of stepping at each one. See <see cref="Lathe.Smooth"/>,
    /// which this is the same flag as and is read by the same shader code.
    /// </summary>
    public bool Smooth { get; init; }

    public override string Kind => "Prism";

    public override void Accept(ISolidVisitor visitor) => visitor.VisitPrism(this);

    public override TResult Accept<TResult>(ISolidVisitor<TResult> visitor) =>
        visitor.VisitPrism(this);
}
