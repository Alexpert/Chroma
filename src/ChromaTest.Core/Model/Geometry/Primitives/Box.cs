using System.Numerics;

namespace ChromaTest.Core.Model.Geometry.Primitives;

/// <summary>
/// An axis-aligned box given by two opposite corners. It is axis-aligned as written;
/// orientation comes from the <c>rotate</c> modifier on <see cref="Solid.Transform"/>.
/// </summary>
public sealed class Box : Solid
{
    public Vector3 Min { get; init; } = new(-1f, -1f, -1f);

    public Vector3 Max { get; init; } = new(1f, 1f, 1f);

    public override string Kind => "Box";

    public override void Accept(ISolidVisitor visitor) => visitor.VisitBox(this);

    public override TResult Accept<TResult>(ISolidVisitor<TResult> visitor) =>
        visitor.VisitBox(this);
}
