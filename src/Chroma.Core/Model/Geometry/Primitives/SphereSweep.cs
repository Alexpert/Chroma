using System.Numerics;

namespace Chroma.Core.Model.Geometry.Primitives;

/// <summary>
/// The volume swept by a sphere whose centre and radius vary along a path — a tube, a cable,
/// a tentacle, a bead of solder.
/// </summary>
/// <remarks>
/// <para>
/// POV-Ray's <c>sphere_sweep</c>, with a linear spline. Each consecutive pair of spheres
/// contributes the convex hull of the two, which is a "round cone": a tapered tube with a
/// hemispherical cap at each end. The sweep is the union of those, and because consecutive
/// hulls share a sphere the joints are seamless without any special treatment.
/// </para>
/// <para>
/// Unlike a prism or a lathe the path is <b>open</b>: it does not close back on itself, so
/// <c>n</c> spheres give <c>n - 1</c> segments rather than <c>n</c>.
/// </para>
/// </remarks>
public sealed class SphereSweep : Solid
{
    /// <summary>Centre and radius of each sphere along the path, in order.</summary>
    public required IReadOnlyList<Vector4> Spheres { get; init; }

    public override string Kind => "SphereSweep";

    public override void Accept(ISolidVisitor visitor) => visitor.VisitSphereSweep(this);

    public override TResult Accept<TResult>(ISolidVisitor<TResult> visitor) =>
        visitor.VisitSphereSweep(this);
}
