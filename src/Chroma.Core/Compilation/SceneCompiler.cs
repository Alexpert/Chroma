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
    /// <param name="shareFrom">
    /// How many appearances a shape needs before it is reached through the instance buffer rather
    /// than written out where it stands. See <see cref="ShapePartition.DefaultShareFrom"/> for why
    /// this is a threshold and not simply "two", and pass
    /// <see cref="ShapePartition.ShareEverything"/> when the driver has refused the program and
    /// the size of it is all that matters.
    /// </param>
    public static CompiledScene? Compile(
        Scene scene,
        DiagnosticBag diagnostics,
        GeometryBackend backend = GeometryBackend.Spans,
        int shareFrom = ShapePartition.DefaultShareFrom)
    {
        return backend == GeometryBackend.DistanceField
            ? CompileDistanceField(scene, diagnostics)
            : CompileSpans(scene, diagnostics, shareFrom);
    }

    private static CompiledScene? CompileSpans(Scene scene, DiagnosticBag diagnostics, int shareFrom)
    {
        // Which roots are the same shape standing somewhere else is settled before anything is
        // emitted, because it decides the frame each shape is written in: a shared shape is
        // emitted at its own origin and placed from a buffer, a singleton where it stands.
        ShapePartition partition = ShapeCanonicalizer.Partition(scene.Roots);
        partition.ShareFrom(shareFrom);

        GeometryEmitter emitter = new(diagnostics);

        foreach (ShapeGroup shape in partition.Shapes)
        {
            emitter.EmitShape(shape);
        }

        if (diagnostics.HasErrors)
        {
            return null;
        }

        (float[] instances, float[] nodes) = emitter.PackInstances();

        return new CompiledScene
        {
            Scene = scene,
            Geometry = emitter.Build(),
            Primitives = [.. emitter.Primitives],
            Materials = [.. emitter.Materials],
            Shapes = [.. emitter.Shapes],
            Instances = instances,
            Nodes = nodes,
            LeafShapes = emitter.LeafShapes,
            ShapeCount = emitter.ShapeCount,
            ShareFrom = shareFrom,
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

            // The distance-field backend does not instance. It is the demonstrator for a
            // different question -- exact intervals against sphere tracing -- and giving it a
            // second axis to differ on would make neither comparison mean anything. Its roots
            // are each their own shape, which is what they were before instancing existed.
            Instances = [],
            Nodes = [],
            LeafShapes = [.. Enumerable.Repeat(-1, emitter.Primitives.Count / GpuLayout.PrimitiveStride)],
            ShapeCount = scene.Roots.Count,

            // Nothing was shared, so there is no threshold that could have shared more and
            // nothing for a driver refusal to retry.
            ShareFrom = ShapePartition.ShareEverything,

            // A distance field has no span lists. Reported as zero rather than left to mean
            // something it cannot mean here.
            WidestRoot = 0,
        };
    }
}
