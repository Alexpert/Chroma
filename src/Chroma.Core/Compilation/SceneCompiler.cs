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
    /// <param name="budget">
    /// What the generated program may weigh. Shapes are shared beyond what
    /// <paramref name="shareFrom"/> already asked for until the estimate fits. See
    /// <see cref="ShapeCost.Budget"/>.
    /// </param>
    public static CompiledScene? Compile(
        Scene scene,
        DiagnosticBag diagnostics,
        GeometryBackend backend = GeometryBackend.Spans,
        int shareFrom = ShapePartition.DefaultShareFrom,
        int budget = ShapeCost.Budget)
    {
        return backend == GeometryBackend.DistanceField
            ? CompileDistanceField(scene, diagnostics)
            : CompileSpans(scene, diagnostics, shareFrom, budget);
    }

    private static CompiledScene? CompileSpans(
        Scene scene, DiagnosticBag diagnostics, int shareFrom, int budget)
    {
        // Which roots are the same shape standing somewhere else is settled before anything is
        // emitted, because it decides the frame each shape is written in: a shared shape is
        // emitted at its own origin and placed from a buffer, a singleton where it stands.
        ShapePartition partition = ShapeCanonicalizer.Partition(scene.Roots);
        partition.Choose(shareFrom, budget);

        GeometryEmitter emitter = new(diagnostics);

        foreach (ShapeGroup shape in partition.Shapes)
        {
            emitter.EmitShape(shape);
        }

        if (diagnostics.HasErrors)
        {
            return null;
        }

        // The seam between guessing and doing. Partition decided what to share on the cost the
        // probe reported, and this is the cost the emitter went on to produce; if they ever
        // disagreed, every decision above would have been made about a scene that is not the one
        // being built, silently and with nothing to point at. They are two runs of one walk, so
        // exact equality is the right assertion rather than a tolerance.
        if (partition.Estimate() != emitter.TotalCost)
        {
            throw new InvalidOperationException(
                $"the partition costed this scene at {partition.Estimate()} statements and the "
                + $"emitter produced {emitter.TotalCost}; the probe and the emission have drifted.");
        }

        (float[] instances, float[] nodes) = emitter.PackInstances();

        return new CompiledScene
        {
            Scene = scene,
            Source = diagnostics.Source,
            Geometry = emitter.Build(),
            Primitives = [.. emitter.Primitives],
            Materials = [.. emitter.Materials],
            Shapes = [.. emitter.Shapes],
            Instances = instances,
            Nodes = nodes,
            LeafShapes = emitter.LeafShapes,
            ShapeCount = emitter.ShapeCount,
            ShapeReports = Report(partition),
            ShareFrom = shareFrom,
            Budget = budget,
            WidestRoot = emitter.WidestRoot,
        };
    }

    /// <summary>What each distinct shape turned out to be, for the console line and the refusal.</summary>
    /// <remarks>
    /// Taken from the group's own root rather than from a placement, because the shape's
    /// definition is what an author would edit to make it cheaper, and the peeled root is where
    /// the geometry actually is: an <c>object { }</c> wrapper carries a position and no cost.
    /// </remarks>
    private static ShapeReport[] Report(ShapePartition partition) =>
    [
        .. partition.Shapes.Select(shape => new ShapeReport(
            shape.Root.Kind.ToLowerInvariant(),
            shape.Root.Origin,
            shape.Root.Generator,
            shape.LeafSlots.Count,
            shape.Cost,
            shape.Placements.Count,
            shape.Instanced)),
    ];

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
            Source = diagnostics.Source,
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

            // Nor is it costed. A sphere-traced program is a different shape of program -- one
            // distance function reached from a march loop -- and a number that looked comparable
            // with the span path's without being it would be worse than no number.
            ShapeReports = [],

            // Nothing was shared, so there is no threshold that could have shared more and
            // nothing for a driver refusal to retry.
            ShareFrom = ShapePartition.ShareEverything,
            Budget = ShapeCost.Budget,

            // A distance field has no span lists. Reported as zero rather than left to mean
            // something it cannot mean here.
            WidestRoot = 0,
        };
    }
}
