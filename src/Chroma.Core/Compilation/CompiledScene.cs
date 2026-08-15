using Chroma.Core.Model;
using Chroma.Core.Sdl.Source;

namespace Chroma.Core.Compilation;

/// <summary>
/// A scene compiled for the GPU: the GLSL that traces it, and the tables it reads. The camera
/// and the lights travel as uniforms rather than in a buffer.
/// </summary>
public sealed class CompiledScene
{
    public required Scene Scene { get; init; }

    /// <summary>
    /// The file this was compiled from, kept so a diagnostic can still name a line.
    /// </summary>
    /// <remarks>
    /// A refusal points at the shapes that caused it, and a <see cref="SourceSpan"/> is a pair of
    /// integers until something holds the text they index. The <see cref="Scene"/> does not: it is
    /// the model, and staying independent of the language is the point of it. A span that names a
    /// file of its own, as one from an included fragment does, uses that instead.
    /// </remarks>
    public required SourceText Source { get; init; }

    /// <summary>
    /// The generated geometry, spliced into raytrace.glsl at its marker.
    /// </summary>
    /// <remarks>
    /// Everything about a shape is in here as constants: the transforms within it, the cone
    /// tapers, the lathe outlines, the span-list sizes and the CSG tree itself. What is not is
    /// <b>where</b> a repeated shape stands. That is in <see cref="Instances"/>, so that the
    /// same body can serve thirty-two pieces without being written thirty-two times into the
    /// driver's assembly.
    /// </remarks>
    public required string Geometry { get; init; }

    /// <summary>
    /// One record per leaf: kind, material, two parameters and the matrix into its local
    /// space, at <see cref="GpuLayout.PrimitiveStride"/> floats each.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Read only when shading. A normal is recomputed once per hit, from whichever surface
    /// turned out to be visible, and that one fetch per bounce is not worth turning into a
    /// branch per leaf in the source. Spans are the hot path and carry no fetch at all.
    /// </para>
    /// <para>
    /// Two fields mean something narrower than they used to, and both narrow the same way: a
    /// leaf of a <b>shared</b> shape belongs to the shape, not to the world. Its matrix reaches
    /// local space from the <i>shape's</i> space rather than from the world, and its material
    /// field is a slot rather than an index. The instance supplies the rest. A leaf of a
    /// singleton is unchanged, world matrix and absolute index, which is what keeps a scene with
    /// nothing to share bit-identical to what it was.
    /// </para>
    /// </remarks>
    public required float[] Primitives { get; init; }

    /// <summary><see cref="GpuLayout.MaterialStride"/> floats each.</summary>
    public required float[] Materials { get; init; }

    /// <summary>
    /// Contour points, blob components and sweep spheres, at
    /// <see cref="GpuLayout.ShapeStride"/> floats per texel. Empty for a scene using none of
    /// them, and read only by the shading path for the same reason as
    /// <see cref="Primitives"/>.
    /// </summary>
    public required float[] Shapes { get; init; }

    /// <summary>
    /// One record per appearance of a shared shape, at <see cref="GpuLayout.InstanceStride"/>
    /// floats each, in the order <see cref="Nodes"/> refers to them. Empty for a scene that
    /// repeats nothing.
    /// </summary>
    /// <remarks>
    /// The one table read by the span path, and the reason a scene is no longer bounded by what
    /// the driver will compile. Everything in it used to be a <c>const mat4</c> in the source,
    /// which is to say it used to be paid for once per placement in the driver's assembly rather
    /// than once per placement in memory.
    /// </remarks>
    public required float[] Instances { get; init; }

    /// <summary>
    /// Which shape each leaf belongs to, as the number the instance record carries, or -1 for a
    /// leaf of a folded shape whose matrix already reaches the world.
    /// </summary>
    /// <remarks>
    /// Not uploaded, and the only entry here that is not. Without it the tables do not describe
    /// themselves: <see cref="Primitives"/> holds a matrix that may be world or may be
    /// shape-local, and nothing says which, or which appearances complete it. That is fine for
    /// the shader, which is told by the code generated around it, and no use at all to anything
    /// on this side that wants to know where a scene's solids actually are.
    /// </remarks>
    public required int[] LeafShapes { get; init; }

    /// <summary>
    /// The BVH over <see cref="Instances"/>, at <see cref="GpuLayout.NodeStride"/> floats each,
    /// depth-first with escape indices. Empty when <see cref="Instances"/> is.
    /// </summary>
    /// <remarks>
    /// Instancing makes the program independent of how many placements a scene holds; this makes
    /// the frame independent too. Without it the shader would test every placement's box against
    /// every ray, which is the linear scan instancing exists to stop paying.
    /// </remarks>
    public required float[] Nodes { get; init; }

    /// <summary>Widest span list any root produces. Reported, not enforced.</summary>
    /// <remarks>
    /// It was a hard limit while every list in the shader was one global size. It is now a
    /// property of the scene the way its primitive count is: worth printing, because it is
    /// the number that most decides how much state a thread carries, and no longer something
    /// a scene can exceed.
    /// </remarks>
    public required int WidestRoot { get; init; }

    public int PrimitiveCount => Primitives.Length / GpuLayout.PrimitiveStride;

    public int MaterialCount => Materials.Length / GpuLayout.MaterialStride;

    public int InstanceCount => Instances.Length / GpuLayout.InstanceStride;

    public int NodeCount => Nodes.Length / GpuLayout.NodeStride;

    /// <summary>
    /// Distinct shapes the scene emits a body for, singletons included.
    /// </summary>
    /// <remarks>
    /// After instancing this is the number that decides whether a scene compiles, where the
    /// primitive count used to be. Worth printing next to it for exactly that reason: a scene of
    /// ten thousand pieces and six shapes is small, and a scene of six pieces and six shapes is
    /// the same size.
    /// </remarks>
    public required int ShapeCount { get; init; }

    /// <summary>
    /// One entry per distinct shape: what it is, where it was written, and what it weighs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not uploaded. It exists so that a driver refusal can name the two or three shapes that
    /// caused it rather than quoting a line count, and so that the console line can say how much
    /// of the budget a scene is spending before it hits the wall rather than after.
    /// </para>
    /// <para>
    /// Empty for the distance-field backend, which is not costed: it emits a different program
    /// with a different shape to it, and a number that looked comparable but was not would be
    /// worse than no number.
    /// </para>
    /// </remarks>
    public required IReadOnlyList<ShapeReport> ShapeReports { get; init; }

    /// <summary>What the generated program weighs, in the units of <see cref="ShapeCost"/>.</summary>
    public int EstimatedCost => ShapeReports.Sum(shape => shape.Total);

    /// <summary>
    /// How many appearances a shape needed before it was reached through the instance buffer.
    /// </summary>
    /// <remarks>
    /// Carried so that a driver refusal can be answered rather than only reported: the threshold
    /// trades program size for speed, and a program the driver will not take has no speed worth
    /// protecting. See <see cref="ShapePartition.DefaultShareFrom"/>.
    /// </remarks>
    public required int ShareFrom { get; init; }

    /// <summary>What the partition was allowed to spend. See <see cref="ShapeCost.Budget"/>.</summary>
    /// <remarks>
    /// Carried for the same reason as <see cref="ShareFrom"/>: a refusal is answered by cutting
    /// this and compiling again, and the answer has to know what was already tried.
    /// </remarks>
    public required int Budget { get; init; }

    /// <summary>Lines of generated GLSL, for the console line.</summary>
    public int GeneratedLines => Geometry.Count(c => c == '\n');

    /// <summary>
    /// Whether any material in the scene transmits light.
    /// </summary>
    /// <remarks>
    /// A shadow ray through glass has to keep going and accumulate a colour, where an opaque
    /// scene's can stop at the first thing in the way. Telling the shader which kind of scene
    /// this is keeps every opaque scene at exactly the cost it had before transmission
    /// existed. Read straight out of the table so it cannot drift from what was uploaded.
    /// </remarks>
    public bool HasTransmission
    {
        get
        {
            for (int i = 0; i < Materials.Length; i += GpuLayout.MaterialStride)
            {
                if (Materials[i + TransmissionOffset] > 0f)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Whether any material in the scene scatters light inside its volume.
    /// </summary>
    /// <remarks>
    /// The same bargain <see cref="HasTransmission"/> strikes, one level up. A scattering
    /// medium turns every segment of every path into an integral that has to be sampled, and a
    /// scene with no medium should not pay for the machinery.
    /// </remarks>
    public bool HasMedia
    {
        get
        {
            for (int i = 0; i < Materials.Length; i += GpuLayout.MaterialStride)
            {
                if (Materials[i + ScatteringOffset] > 0f)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>Floats into a material entry to reach <c>transmission</c>.</summary>
    private const int TransmissionOffset = 2 * 4 + 3;

    /// <summary>Floats into a material entry to reach <c>scattering</c>.</summary>
    private const int ScatteringOffset = 3 * 4 + 1;
}
