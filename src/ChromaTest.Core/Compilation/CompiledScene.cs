using ChromaTest.Core.Model;

namespace ChromaTest.Core.Compilation;

/// <summary>
/// A scene flattened into the three arrays the shader reads, plus the camera and lights,
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

    /// <summary>Floats into a material entry to reach <c>transmission</c>.</summary>
    private const int TransmissionOffset = 2 * 4 + 3;
}
