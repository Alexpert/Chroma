using Chroma.Core.Model;

namespace Chroma.Core.Compilation;

/// <summary>
/// A scene flattened into the four arrays the shader reads, plus the camera and lights,
/// which travel as uniforms rather than in a buffer.
/// </summary>
public sealed class CompiledScene
{
    public required Scene Scene { get; init; }

    /// <summary>Post-order instruction tape, <see cref="GpuLayout.TapeStride"/> ints each.</summary>
    public required int[] Tape { get; init; }

    /// <summary><see cref="GpuLayout.PrimitiveStride"/> floats each.</summary>
    public required float[] Primitives { get; init; }

    /// <summary><see cref="GpuLayout.MaterialStride"/> floats each.</summary>
    public required float[] Materials { get; init; }

    /// <summary>
    /// Variable-length shape data — prism and lathe edges, blob components — as
    /// <see cref="GpuLayout.ShapeStride"/> floats per texel. Empty for a scene that uses
    /// none of those three.
    /// </summary>
    public required float[] Shapes { get; init; }

    public required SpanBudget Budget { get; init; }

    public int InstructionCount => Tape.Length / GpuLayout.TapeStride;

    public int PrimitiveCount => Primitives.Length / GpuLayout.PrimitiveStride;

    public int MaterialCount => Materials.Length / GpuLayout.MaterialStride;

    /// <summary>
    /// Whether any material in the scene transmits light.
    /// </summary>
    /// <remarks>
    /// A shadow ray through glass has to keep going and accumulate a colour, where an opaque
    /// scene's can stop at the first thing in the way. Telling the shader which kind of
    /// scene this is keeps every opaque scene at exactly the cost it had before transmission
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
    /// medium turns every segment of every path into an integral that has to be sampled, and
    /// a scene with no medium should not pay for the machinery: told this is 0, the shader
    /// keeps iteration 5's straight walk from surface to surface. Read from the table, where
    /// the compiler has already zeroed the scattering of anything light cannot enter.
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

    /// <summary>
    /// Whether the tape carries any bounding-box guard.
    /// </summary>
    /// <remarks>
    /// The shader must be compiled to agree with this, and agreement is not optional in either
    /// direction. Told there are none when there are, it would read a guard as an unknown
    /// opcode and fall into the operator branch, popping two lists that were never pushed.
    /// Told there are some when there are none, it pays for a branch it never takes — which
    /// sounds free and is not: iteration 11 measured that costing a scene a factor of two.
    /// The compiler only emits guards where they can pay, so this is usually false for a scene
    /// written by hand and true for one written with a loop.
    /// </remarks>
    public bool HasBounds
    {
        get
        {
            for (int i = 0; i < Tape.Length; i += GpuLayout.TapeStride)
            {
                if (Tape[i] == (int)TapeOpcode.Bound)
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
