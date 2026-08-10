using System.Numerics;

namespace Chroma.Core.Model.Geometry.Primitives;

/// <summary>
/// A closed polygon in the XZ plane, swept along Y between two heights and capped.
/// </summary>
/// <remarks>
/// <para>
/// POV-Ray's <c>prism</c>, restricted to a linear spline and to a single contour. The
/// curved spline types are a CPU-side tessellation and are not built yet; the sub-contour
/// mechanism, which POV-Ray uses to punch holes by even-odd overlap, is not needed here
/// because this renderer has real CSG — write the hole as a <c>difference</c>.
/// </para>
/// <para>
/// The contour is closed implicitly: the last point joins back to the first.
/// </para>
/// </remarks>
public sealed class Prism : Solid
{
    public float Bottom { get; init; }

    public float Top { get; init; } = 1f;

    /// <summary>Vertices in the XZ plane, in order, without repeating the first.</summary>
    public required IReadOnlyList<Vector2> Points { get; init; }

    public override string Kind => "Prism";

    public override void Accept(ISolidVisitor visitor) => visitor.VisitPrism(this);

    public override TResult Accept<TResult>(ISolidVisitor<TResult> visitor) =>
        visitor.VisitPrism(this);
}
