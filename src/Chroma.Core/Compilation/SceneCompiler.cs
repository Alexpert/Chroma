using Chroma.Core.Model;
using Chroma.Core.Model.Geometry;
using Chroma.Core.Sdl.Source;

namespace Chroma.Core.Compilation;

/// <summary>
/// Turns a loaded <see cref="Scene"/> into the buffers the shader reads.
/// </summary>
public static class SceneCompiler
{
    /// <summary>
    /// Returns null when the scene cannot be compiled, having reported why into
    /// <paramref name="diagnostics"/>.
    /// </summary>
    public static CompiledScene? Compile(Scene scene, DiagnosticBag diagnostics)
    {
        // Whether the tape carries bounding-box guards is settled here, before a single
        // instruction is emitted, because it is a property of the scene rather than of any
        // subtree in it — see CsgTapeBuilder.GuardsPayFrom. Counting from the model costs one
        // walk of a tree that is about to be walked anyway, and avoids the alternative of
        // building the tape twice to find out how long it is.
        int instructionEstimate = scene.Roots.Sum(root => CsgTapeBuilder.InstructionsFor(root) + 1);
        bool guarded = instructionEstimate >= CsgTapeBuilder.GuardsPayFrom;

        CsgTapeBuilder builder = new(diagnostics, guarded);
        SpanBudget budget = SpanBudget.None;

        foreach (Solid root in scene.Roots)
        {
            SpanBudget rootBudget = builder.Descend(root).Budget;
            builder.CloseRoot();

            // A max, not a sum: the shader resolves each root separately and reuses the
            // same arrays, so what matters is the most demanding root rather than their
            // total. That is what lets a scene hold any number of separate solids.
            budget = new SpanBudget(
                Math.Max(budget.Spans, rootBudget.Spans),
                Math.Max(budget.StackDepth, rootBudget.StackDepth));
        }

        int instructions = builder.Tape.Count / GpuLayout.TapeStride;
        if (instructions > GpuLayout.MaxInstructions)
        {
            // No single solid is at fault, so this points at the start of the file rather
            // than picking one arbitrarily.
            diagnostics.Error(
                default,
                $"the scene compiles to {instructions} tape instructions; "
                + $"the limit is {GpuLayout.MaxInstructions}");
        }

        if (diagnostics.HasErrors)
        {
            return null;
        }

        return new CompiledScene
        {
            Scene = scene,
            Tape = [.. builder.Tape],
            Primitives = [.. builder.Primitives],
            Materials = [.. builder.Materials],
            Shapes = [.. builder.Shapes],
            Budget = budget,
        };
    }
}
