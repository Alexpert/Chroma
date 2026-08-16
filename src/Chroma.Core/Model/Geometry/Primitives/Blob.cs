using System.Numerics;

namespace Chroma.Core.Model.Geometry.Primitives;

/// <summary>
/// One field source of a <see cref="Blob"/>: a capsule, of which a sphere is the case whose
/// two ends are the same point.
/// </summary>
/// <remarks>
/// One record for both kinds rather than two, because the field is one formula either way. The
/// distance a component's field falls off with is the distance to the <b>segment</b>
/// <see cref="Base"/> to <see cref="Cap"/>, and the distance to a segment of no length is the
/// distance to its point. So a <c>blobSphere</c> is written here with both ends together, and
/// nothing downstream needs a discriminator to shade it or to bound it.
/// </remarks>
/// <param name="Base">One end of the field's axis, in the blob's own space.</param>
/// <param name="Cap">The other end, equal to <paramref name="Base"/> for a spherical source.</param>
/// <param name="Radius">Distance from the axis at which the field falls to zero.</param>
/// <param name="Strength">
/// Field value on the axis. A negative strength hollows the blob out where it overlaps a
/// positive one instead of adding to it.
/// </param>
public readonly record struct BlobComponent(
    Vector3 Base,
    Vector3 Cap,
    float Radius,
    float Strength)
{
    /// <summary>Whether the two ends are one point, and so whether the source is a sphere.</summary>
    public bool IsSphere => Base == Cap;
}

/// <summary>
/// An isosurface of a sum of fields — the only primitive here that is not defined by an
/// equation of its own surface.
/// </summary>
/// <remarks>
/// <para>
/// POV-Ray's <c>blob</c>. A component's field falls off as
/// <c>strength · (1 − (d/radius)²)²</c> with <c>d</c> the distance to its axis segment, which
/// is a quartic in the ray parameter, and the surface is where the sum of the active components
/// reaches <see cref="Threshold"/>.
/// </para>
/// <para>
/// A cylindrical component costs the tracing more than a spherical one but not differently in
/// kind. The distance to a segment is piecewise in three regions, and the piece that applies
/// changes where the ray crosses one of the two planes through the ends, so those two crossings
/// join the component's own entry and exit as places the summed polynomial has to be rebuilt.
/// Inside every region <c>d²</c> is still quadratic in the ray parameter, so the field is still
/// a quartic and the solver is untouched.
/// </para>
/// <para>
/// Components attract each other exactly as in POV-Ray: two that overlap merge into one smooth
/// surface rather than showing a seam, because the surface is a property of the sum and not of
/// either source.
/// </para>
/// </remarks>
public sealed class Blob : Solid
{
    /// <summary>Field value the surface sits at. Above it is inside.</summary>
    public float Threshold { get; init; } = 1f;

    public required IReadOnlyList<BlobComponent> Components { get; init; }

    public override string Kind => "Blob";

    public override void Accept(ISolidVisitor visitor) => visitor.VisitBlob(this);

    public override TResult Accept<TResult>(ISolidVisitor<TResult> visitor) =>
        visitor.VisitBlob(this);
}
