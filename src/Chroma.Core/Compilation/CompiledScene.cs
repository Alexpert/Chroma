using Chroma.Core.Model;

namespace Chroma.Core.Compilation;

/// <summary>
/// A scene compiled for the GPU: the GLSL that traces it, and the two tables the shading half
/// still reads. The camera and the lights travel as uniforms rather than in a buffer.
/// </summary>
public sealed class CompiledScene
{
    public required Scene Scene { get; init; }

    /// <summary>
    /// The generated geometry, spliced into raytrace.glsl at its marker.
    /// </summary>
    /// <remarks>
    /// Everything about the scene's shape is in here as constants: the transforms, the cone
    /// tapers, the lathe outlines, the bounding boxes, the span-list sizes and the CSG tree
    /// itself. Nothing about its shape is in a buffer any more.
    /// </remarks>
    public required string Geometry { get; init; }

    /// <summary>
    /// One record per leaf — kind, material index, two parameters and the world-to-local
    /// matrix — at <see cref="GpuLayout.PrimitiveStride"/> floats each.
    /// </summary>
    /// <remarks>
    /// Read only when shading. A normal is recomputed once per hit, from whichever surface
    /// turned out to be visible, and that one fetch per bounce is not worth turning into a
    /// branch per leaf in the source. Spans are the hot path and carry no fetch at all.
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
