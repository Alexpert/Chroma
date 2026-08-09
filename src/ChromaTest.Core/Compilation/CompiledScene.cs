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
}
