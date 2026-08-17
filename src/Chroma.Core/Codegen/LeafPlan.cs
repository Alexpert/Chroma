using System.Numerics;
using Chroma.Core.Compilation;

namespace Chroma.Core.Codegen;

/// <summary>
/// Everything <see cref="LeafEmitter"/> needs about one leaf, gathered by the visitor that
/// walked the tree so the emitter never has to know what a <c>Lathe</c> is.
/// </summary>
/// <param name="Index">
/// The leaf's index in the scene. It is what a span's surface code names, and what the normal
/// and material lookups index the primitive table by, so it is the one number the generated
/// code and the uploaded buffer both have to agree on.
/// </param>
/// <param name="Spans">Spans this leaf can produce along one ray. Exact, and no longer clamped.</param>
/// <param name="ToLocal">World to the primitive's canonical space, already composed and inverted.</param>
/// <param name="Contours">
/// How <paramref name="Points"/> divides into closed contours, as the size of each. Empty for a
/// primitive that is not defined by one, and one entry for the ordinary prism or lathe.
/// </param>
/// <param name="Balls">
/// A sweep's path spheres as <c>(centre, radius)</c>, or a blob's components as
/// <c>(base, radius)</c>. Paired with <paramref name="Caps"/> for the blob and unpaired for the
/// sweep, which is also how the two are laid out in the shape buffer.
/// </param>
/// <param name="Caps">
/// A blob component's far end and its strength, as <c>(cap, strength)</c>. A spherical component
/// has its <c>cap</c> equal to its <c>base</c>, so the whole of what tells the two kinds apart is
/// whether these two entries agree on a point.
/// </param>
/// <param name="Signature">
/// What makes two leaves of this shape different when nothing else here does. Empty for every
/// primitive whose geometry is in the numbers above.
/// </param>
/// <remarks>
/// <see cref="Signature"/> exists for the mesh alone and is not a general escape hatch. Every
/// other primitive puts its geometry in this record and therefore into the GLSL it emits, which
/// is what lets two leaves be compared by comparing their bodies. A mesh's geometry is in a
/// buffer and its body carries an offset, so two meshes emit the same text inside a probe, where
/// every buffer starts empty. This is the thing that tells them apart. See
/// <c>MeshFile.Signature</c>.
/// </remarks>
internal readonly record struct LeafPlan(
    int Index,
    PrimitiveKind Kind,
    int Spans,
    Matrix4x4 ToLocal,
    float ParamA,
    IReadOnlyList<Vector2> Points,
    IReadOnlyList<int> Contours,
    IReadOnlyList<Vector4> Balls,
    IReadOnlyList<Vector4> Caps,
    string Comment,
    string Signature = "");
