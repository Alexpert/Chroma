using Chroma.Core.Compilation;
using Chroma.Core.Model.Materials;

namespace Chroma.Core.Codegen;

/// <summary>
/// The tables every chunk of a scene shares, and the interning that keeps them small.
/// </summary>
/// <remarks>
/// <para>
/// A scene too large for one program is compiled as several, one per chunk of geometry, and the
/// split is in the <b>code</b> only. These tables are data: they are uploaded once, read by the
/// shading half, and indexed the same way whichever chunk produced the hit. So one of these is
/// built for the whole scene and handed to every chunk's <see cref="GeometryEmitter"/>.
/// </para>
/// <para>
/// It is not merely tidier, it is required. A leaf's index in <see cref="Primitives"/> is written
/// into the generated GLSL as a literal — the function is called <c>leaf17</c> and the span it
/// produces is tagged <c>17</c> — so the numbering has to be scene-wide or a hit in the second
/// chunk would name the first chunk's primitive and be shaded as the wrong solid. Materials are
/// interned across chunks for the same reason and one more: a run shared by two chunks costs one
/// entry rather than two.
/// </para>
/// <para>
/// A scene that fits in one program has one chunk, one of these, and every index in it is what it
/// was before chunking existed.
/// </para>
/// </remarks>
internal sealed class SceneTables
{
    /// <summary>One record per leaf, at <see cref="GpuLayout.PrimitiveStride"/> floats each.</summary>
    public List<float> Primitives { get; } = [];

    /// <summary><see cref="GpuLayout.MaterialStride"/> floats each.</summary>
    public List<float> Materials { get; } = [];

    /// <summary>
    /// Contour points, blob components, sweep spheres, mesh triangles and height samples.
    /// </summary>
    public List<float> Shapes { get; } = [];

    /// <summary>
    /// Where each distinct mesh's or height field's block starts, keyed on its signature.
    /// </summary>
    /// <remarks>
    /// The only table entry that is interned by <b>content</b> rather than appended per leaf, and
    /// the reason is size: a contour is a handful of texels and a bunny is a hundred thousand, so
    /// writing one per placement is the difference between a scene that fits on the card and one
    /// that does not. Eight teapots therefore upload one teapot. The two kinds share this table
    /// because a signature already names a content and nothing distinguishes theirs.
    /// </remarks>
    public Dictionary<string, int> BlockOffsets { get; } = [];

    /// <summary>A stable number per distinct material, for naming a run rather than finding one.</summary>
    public Dictionary<Material, int> MaterialIndices { get; } = [];

    /// <summary>Where a run of materials belonging to one appearance starts, keyed on the run.</summary>
    public Dictionary<string, int> MaterialRuns { get; } = [];

    /// <summary>How many leaves have been written, which is the index the next one gets.</summary>
    public int LeafCount => Primitives.Count / GpuLayout.PrimitiveStride;
}
