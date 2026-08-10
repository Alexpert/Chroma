using System.Numerics;

namespace ChromaTest.Core.Model.Geometry.Primitives;

/// <summary>
/// One spherical field source of a <see cref="Blob"/>.
/// </summary>
/// <param name="Center">Centre of the field, in the blob's own space.</param>
/// <param name="Radius">Distance at which the field falls to zero.</param>
/// <param name="Strength">
/// Field value at the centre. A negative strength hollows the blob out where it overlaps a
/// positive one instead of adding to it.
/// </param>
public readonly record struct BlobSphere(Vector3 Center, float Radius, float Strength);

/// <summary>
/// An isosurface of a sum of spherical fields — the only primitive here that is not defined
/// by an equation of its own surface.
/// </summary>
/// <remarks>
/// <para>
/// POV-Ray's <c>blob</c>, with spherical components only. Its field falls off as
/// <c>strength · (1 − (d/radius)²)²</c>, which is a quartic in the ray parameter, and the
/// surface is where the sum of the active components reaches <see cref="Threshold"/>.
/// Cylindrical components are not built: their field is piecewise in a way the spherical one
/// is not, and each piece would need its own solve.
/// </para>
/// <para>
/// Components attract each other exactly as in POV-Ray: two spheres that overlap merge into
/// one smooth surface rather than showing a seam, because the surface is a property of the
/// sum and not of either sphere.
/// </para>
/// </remarks>
public sealed class Blob : Solid
{
    /// <summary>Field value the surface sits at. Above it is inside.</summary>
    public float Threshold { get; init; } = 1f;

    public required IReadOnlyList<BlobSphere> Components { get; init; }

    public override string Kind => "Blob";

    public override void Accept(ISolidVisitor visitor) => visitor.VisitBlob(this);

    public override TResult Accept<TResult>(ISolidVisitor<TResult> visitor) =>
        visitor.VisitBlob(this);
}
