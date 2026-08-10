using System.Numerics;

namespace ChromaTest.Core.Model.Geometry.Primitives;

/// <summary>
/// An infinite half-space: everything on the side the normal points away from.
/// </summary>
/// <remarks>
/// <para>
/// This is the first unbounded solid in the renderer, and it is a solid rather than a
/// surface — <c>difference { plane, sphere }</c> is a ground with a crater in it, which is
/// only meaningful because the plane has an inside.
/// </para>
/// <para>
/// The consequence for the span machinery is that one end of its span sits at infinity and
/// bounds no surface. The <c>surf == 0</c> encoding already reserved for the ends of a
/// complement covers exactly that case, so nothing new was needed for it.
/// </para>
/// </remarks>
public sealed class Plane : Solid
{
    /// <summary>Outward normal; the solid is on the side this points away from.</summary>
    public Vector3 Normal { get; init; } = Vector3.UnitY;

    /// <summary>Signed distance from the origin along <see cref="Normal"/>.</summary>
    public float Distance { get; init; }

    public override string Kind => "Plane";

    public override void Accept(ISolidVisitor visitor) => visitor.VisitPlane(this);

    public override TResult Accept<TResult>(ISolidVisitor<TResult> visitor) =>
        visitor.VisitPlane(this);
}
