using System.Numerics;

namespace ChromaTest.Core.Model.Geometry.Primitives;

public sealed class Sphere : Solid
{
    public Vector3 Center { get; init; } = Vector3.Zero;

    public float Radius { get; init; } = 1f;

    public override string Kind => "Sphere";

    public override void Accept(ISolidVisitor visitor) => visitor.VisitSphere(this);

    public override TResult Accept<TResult>(ISolidVisitor<TResult> visitor) =>
        visitor.VisitSphere(this);
}
