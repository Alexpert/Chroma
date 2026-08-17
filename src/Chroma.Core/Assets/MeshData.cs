using System.Numerics;

namespace Chroma.Core.Assets;

/// <summary>
/// A triangle mesh as it comes out of a file, before anything has asked whether it is a solid.
/// </summary>
/// <remarks>
/// <para>
/// Indexed rather than a triangle soup, and that is not only a size decision. Every question
/// <see cref="MeshTopology"/> asks — is this closed, is it wound consistently, where are its
/// holes — is a question about which triangles <b>share</b> a vertex, and a soup cannot answer
/// it. An STL has no indices at all, so <see cref="StlReader"/> welds one before handing it
/// over; without that step every edge of every STL would look like a boundary edge.
/// </para>
/// <para>
/// <see cref="Normals"/> is per vertex and parallel to <see cref="Positions"/>, or null when the
/// file supplied none. Nothing here derives them: whether they are wanted at all is the scene's
/// decision, taken by the <c>smooth</c> field, so the derivation lives in
/// <see cref="MeshTopology.Build"/> where that flag is known.
/// </para>
/// <para>
/// UV coordinates are read past and discarded. Nothing consumes them until textures land, and
/// two floats per vertex uploaded for no reader is bandwidth spent on nothing. The place they
/// would enter is <see cref="ObjReader"/>'s <c>vt</c> line, which already parses them.
/// </para>
/// </remarks>
/// <param name="Positions">Vertex positions in the file's own space, in the file's order.</param>
/// <param name="Indices">Three per triangle, counter-clockwise seen from outside.</param>
/// <param name="Normals">One per vertex, parallel to <paramref name="Positions"/>, or null.</param>
public sealed record MeshData(
    IReadOnlyList<Vector3> Positions,
    IReadOnlyList<int> Indices,
    IReadOnlyList<Vector3>? Normals)
{
    public int TriangleCount => Indices.Count / 3;
}
