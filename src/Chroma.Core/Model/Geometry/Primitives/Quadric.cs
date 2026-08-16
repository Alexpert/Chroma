using System.Numerics;

namespace Chroma.Core.Model.Geometry.Primitives;

/// <summary>
/// The solid where a general quadratic in x, y and z is at most zero.
/// </summary>
/// <remarks>
/// <para>
/// POV-Ray's <c>quadric</c>. The surface is
/// <c>A x² + B y² + C z² + D xy + E xz + F yz + G x + H y + I z + J = 0</c>, and the inside is
/// where that expression is <b>negative</b>, which makes <c>x² + y² + z² − 1 ≤ 0</c> the unit
/// ball and agrees with the sign convention every distance function here uses.
/// </para>
/// <para>
/// It sits <i>beside</i> the sphere, the cylinder and the cone rather than replacing them.
/// Those three are cases of this one, but they are cases with a slab, a known bound and a span
/// function of a few lines each, and re-expressing them here would cost every scene that uses
/// them instructions to buy nothing. What this adds is the rest of the family: ellipsoids that
/// are not spheres, paraboloids, and the hyperboloids that no combination of the others makes.
/// </para>
/// <para>
/// <b>It is generally unbounded</b>, and that is not a defect to be repaired. A hyperbolic
/// paraboloid genuinely runs to infinity, exactly as <see cref="Plane"/> does, and it takes the
/// same infinite box. A ray through one has to be tested against it wherever it goes, which is
/// correct and slow; the fix is in the language rather than in the primitive, since
/// <c>intersection { quadric { … } box { … } }</c> takes the box's bounds and is what POV-Ray's
/// <c>bounded_by</c> is for.
/// </para>
/// </remarks>
public sealed class Quadric : Solid
{
    /// <summary>Coefficients of <c>x²</c>, <c>y²</c> and <c>z²</c>.</summary>
    public Vector3 Squared { get; init; } = Vector3.One;

    /// <summary>Coefficients of <c>xy</c>, <c>xz</c> and <c>yz</c>.</summary>
    public Vector3 Mixed { get; init; }

    /// <summary>Coefficients of <c>x</c>, <c>y</c> and <c>z</c>.</summary>
    public Vector3 Linear { get; init; }

    /// <summary>The constant term. With the defaults above, the unit sphere.</summary>
    public float Constant { get; init; } = -1f;

    public override string Kind => "Quadric";

    public override void Accept(ISolidVisitor visitor) => visitor.VisitQuadric(this);

    public override TResult Accept<TResult>(ISolidVisitor<TResult> visitor) =>
        visitor.VisitQuadric(this);
}
