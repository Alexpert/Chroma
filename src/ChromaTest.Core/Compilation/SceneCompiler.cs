using ChromaTest.Core.Model;
using ChromaTest.Core.Model.Geometry;
using ChromaTest.Core.Sdl.Source;

namespace ChromaTest.Core.Compilation;

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
        CsgTapeBuilder builder = new(diagnostics);
        SpanBudget budget = SpanBudget.None;

        foreach (Solid root in scene.Roots)
        {
            SpanBudget rootBudget = builder.Descend(root);

            // Top-level solids are implicitly unioned, so their spans add up while their
            // stack requirements only ever overlap.
            budget = new SpanBudget(
                budget.Spans + rootBudget.Spans,
                Math.Max(budget.StackDepth, rootBudget.StackDepth));
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
            Budget = budget,
        };
    }
}
