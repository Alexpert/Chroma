using Chroma.Core.Model.Geometry.Operations;
using Chroma.Core.Model.Geometry.Primitives;

namespace Chroma.Core.Model.Geometry;

/// <summary>
/// Traversal for visitors that act by side effect, such as the hierarchy printer.
/// </summary>
/// <remarks>
/// This exists alongside <see cref="ISolidVisitor{TResult}"/> because C# does not allow a
/// generic argument of <c>void</c>. Threading a dummy result type through every printer
/// method would be worse than the small duplication of two interfaces.
/// </remarks>
public interface ISolidVisitor
{
    void VisitSphere(Sphere sphere);

    void VisitBox(Box box);

    void VisitCylinder(Cylinder cylinder);

    void VisitCone(Cone cone);

    void VisitPlane(Plane plane);

    void VisitTorus(Torus torus);

    void VisitPrism(Prism prism);

    void VisitLathe(Lathe lathe);

    void VisitBlob(Blob blob);

    void VisitSphereSweep(SphereSweep sweep);

    void VisitQuadric(Quadric quadric);

    void VisitMesh(Mesh mesh);

    void VisitUnion(Union union);

    void VisitIntersection(Intersection intersection);

    void VisitDifference(Difference difference);
}

/// <summary>
/// Traversal for visitors that produce a value, such as the GPU tape compiler.
/// </summary>
public interface ISolidVisitor<out TResult>
{
    TResult VisitSphere(Sphere sphere);

    TResult VisitBox(Box box);

    TResult VisitCylinder(Cylinder cylinder);

    TResult VisitCone(Cone cone);

    TResult VisitPlane(Plane plane);

    TResult VisitTorus(Torus torus);

    TResult VisitPrism(Prism prism);

    TResult VisitLathe(Lathe lathe);

    TResult VisitBlob(Blob blob);

    TResult VisitSphereSweep(SphereSweep sweep);

    TResult VisitQuadric(Quadric quadric);

    TResult VisitMesh(Mesh mesh);

    TResult VisitUnion(Union union);

    TResult VisitIntersection(Intersection intersection);

    TResult VisitDifference(Difference difference);
}
