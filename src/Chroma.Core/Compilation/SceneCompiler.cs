using Chroma.Core.Codegen;
using Chroma.Core.Model;
using Chroma.Core.Model.Geometry;
using Chroma.Core.Sdl.Source;

namespace Chroma.Core.Compilation;

/// <summary>
/// Turns a loaded <see cref="Scene"/> into the GLSL that traces it, plus the two tables the
/// shading half still reads.
/// </summary>
/// <remarks>
/// Until iteration 12 this produced a post-order instruction tape for a generic shader to
/// interpret. It now produces source: the tree is emitted as nested calls over named locals,
/// each sized to its own node. See documents/code-generation.md for why that reverses the
/// decision recorded in documents/architecture.md.
/// </remarks>
public static class SceneCompiler
{
    /// <summary>
    /// Returns null when the scene cannot be compiled, having reported why into
    /// <paramref name="diagnostics"/>.
    /// </summary>
    public static CompiledScene? Compile(Scene scene, DiagnosticBag diagnostics)
    {
        GeometryEmitter emitter = new(diagnostics);

        foreach (Solid root in scene.Roots)
        {
            emitter.EmitRoot(root);
        }

        if (diagnostics.HasErrors)
        {
            return null;
        }

        return new CompiledScene
        {
            Scene = scene,
            Geometry = emitter.Build(),
            Primitives = [.. emitter.Primitives],
            Materials = [.. emitter.Materials],
            Shapes = [.. emitter.Shapes],
            WidestRoot = emitter.WidestRoot,
        };
    }
}
