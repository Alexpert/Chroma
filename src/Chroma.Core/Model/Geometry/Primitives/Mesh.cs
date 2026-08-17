using System.Numerics;

namespace Chroma.Core.Model.Geometry.Primitives;

/// <summary>
/// A closed triangle mesh loaded from a file, standing beside the analytic primitives as an
/// ordinary CSG operand.
/// </summary>
/// <remarks>
/// <para>
/// The first primitive whose parameter is a <b>file</b> rather than a handful of numbers, and the
/// first that can be refused for what it contains rather than for what a field says. A triangle
/// soup has no inside; a closed, manifold, consistently oriented mesh has one, by the parity of a
/// ray's crossings. <c>Chroma.Core.Assets.MeshTopology</c> is what decides which of the two a
/// file is, and nothing past this point re-asks: a <see cref="Mesh"/> that exists is a solid.
/// </para>
/// <para>
/// The vertices are in the file's own space. Unlike every other primitive here there is no
/// canonical form to fold into the matrix — a mesh is not a unit anything — so the emitter's
/// bounding box comes off the positions and the transform is whatever the scene wrote.
/// </para>
/// <para>
/// <see cref="MaxSpans"/> is the one field that budgets rather than describes. A mesh's true
/// worst case is one span per two triangles, which is not a span-list width anyone can afford, so
/// the bound is declared by the scene. It is the same relaxation documents/csg-raytracing.md
/// already records for tessellated curves, made explicit and per shape.
/// </para>
/// </remarks>
public sealed class Mesh : Solid
{
    /// <summary>Vertex positions, in the file's own space.</summary>
    public required IReadOnlyList<Vector3> Positions { get; init; }

    /// <summary>Three per triangle, counter-clockwise seen from outside.</summary>
    public required IReadOnlyList<int> Indices { get; init; }

    /// <summary>
    /// One normal per vertex, parallel to <see cref="Positions"/>, or null for a mesh shaded by
    /// its faces.
    /// </summary>
    /// <remarks>
    /// Present exactly when <c>smooth</c> was asked for. Interpolating these across a triangle is
    /// iteration 7's lesson repeated: the tessellation is in the shading before it is in the
    /// silhouette, and a faceted mesh reads as a coarse mesh when it is a missing interpolation.
    /// </remarks>
    public IReadOnlyList<Vector3>? Normals { get; init; }

    /// <summary>How many separate stretches of one ray may lie inside this mesh.</summary>
    public required int MaxSpans { get; init; }

    /// <summary>
    /// What makes two meshes the same mesh, from <c>MeshFile.Signature</c>.
    /// </summary>
    /// <remarks>
    /// Carried on the solid because the emitter needs it and cannot recompute it cheaply: it is
    /// what stops two different meshes, which emit the same GLSL but for a buffer offset, from
    /// being taken for one shape. See <c>MeshFile.Signature</c> for why that would otherwise
    /// happen.
    /// </remarks>
    public required string Signature { get; init; }

    /// <summary>Where it was loaded from, for anything that has to name it.</summary>
    public required string Path { get; init; }

    public int TriangleCount => Indices.Count / 3;

    public override string Kind => "Mesh";

    public override void Accept(ISolidVisitor visitor) => visitor.VisitMesh(this);

    public override TResult Accept<TResult>(ISolidVisitor<TResult> visitor) =>
        visitor.VisitMesh(this);
}
