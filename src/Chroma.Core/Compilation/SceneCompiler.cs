using Chroma.Core.Codegen;
using Chroma.Core.Model;
using Chroma.Core.Model.Geometry;
using Chroma.Core.Sdl.Source;

namespace Chroma.Core.Compilation;

/// <summary>
/// How a scene's geometry is found along a ray.
/// </summary>
/// <remarks>
/// <para>
/// Iteration 0 chose exact analytic intervals over distance fields on reasoning alone, and
/// nothing tested the choice for thirteen iterations. This enum is what makes it testable: both
/// backends write the same <see cref="CompiledScene.Geometry"/> slot, are spliced at the same
/// marker, and answer the same two functions. Everything above the seam is therefore shared: the
/// path tracer, the BRDF, next-event estimation, media and accumulation all stay as they are, and
/// the two backends differ in exactly one thing. See documents/raymarching.md.
/// </para>
/// </remarks>
public enum GeometryBackend
{
    /// <summary>Exact ray/solid intervals. What the renderer has always used.</summary>
    Spans = 0,

    /// <summary>Sphere tracing a signed distance field. The demonstrator.</summary>
    DistanceField = 1,
}

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
    public static CompiledScene? Compile(
        Scene scene,
        DiagnosticBag diagnostics,
        GeometryBackend backend = GeometryBackend.Spans)
    {
        return backend == GeometryBackend.DistanceField
            ? CompileDistanceField(scene, diagnostics)
            : CompileSpans(scene, diagnostics);
    }

    private static CompiledScene? CompileSpans(Scene scene, DiagnosticBag diagnostics)
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

    private static CompiledScene? CompileDistanceField(Scene scene, DiagnosticBag diagnostics)
    {
        SdfEmitter emitter = new(diagnostics);

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

            // A distance field has no span lists. Reported as zero rather than left to mean
            // something it cannot mean here.
            WidestRoot = 0,
        };
    }
}
