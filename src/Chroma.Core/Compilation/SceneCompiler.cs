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
        //
        // And a shape too big for any program to hold is cut into the operands of its own union
        // first, which is what lets the question be asked of the pieces at all: cube.chroma is one
        // shape until it is cut and twenty appearances of one shape afterwards. A scene that fits
        // comes back from this exactly as ShapeCanonicalizer.Partition would have returned it.
        ShapePartition partition = RootSplitter.Cut(scene.Roots, shareFrom, budget);

        // And only then, for a scene that still does not fit, how many programs it takes. Sharing
        // first and splitting second is the right order: sharing costs a buffer read, splitting
        // costs running the path tracer once more per chunk.
        IReadOnlyList<IReadOnlyList<ShapeGroup>> split = SceneChunker.Split(partition, budget);

        // One set of tables for the whole scene however many programs read them. A leaf's index
        // is a literal in the generated GLSL, so the numbering has to run across chunks.
        SceneTables tables = new();

        List<CompiledChunk> chunks = [];
        List<float> instances = [];
        List<float> nodes = [];
        List<int> leafShapes = [];

        int total = 0;

        foreach (IReadOnlyList<ShapeGroup> shapes in split)
        {
            GeometryEmitter emitter = new(
                diagnostics,
                tables,
                sharedIdBase: SharedSoFar(split, chunks.Count),
                nodeBase: nodes.Count / GpuLayout.NodeStride);

            foreach (ShapeGroup shape in shapes)
            {
                emitter.EmitShape(shape);
            }

            if (diagnostics.HasErrors)
            {
                return null;
            }

            (float[] chunkInstances, float[] chunkNodes) =
                emitter.PackInstances(instances.Count / GpuLayout.InstanceStride);

            chunks.Add(new CompiledChunk
            {
                Geometry = emitter.Build(),
                ShapeReports = Report(shapes),
                ShapeCount = emitter.ShapeCount,
                NodeBase = nodes.Count / GpuLayout.NodeStride,
                NodeCount = chunkNodes.Length / GpuLayout.NodeStride,
                WidestRoot = emitter.WidestRoot,
            });

            instances.AddRange(chunkInstances);
            nodes.AddRange(chunkNodes);
            leafShapes.AddRange(emitter.LeafShapes);

            total += emitter.TotalCost;
        }

        // The seam between guessing and doing. Partition decided what to share on the cost the
        // probe reported, and this is the cost the emitters went on to produce; if they ever
        // disagreed, every decision above would have been made about a scene that is not the one
        // being built, silently and with nothing to point at. They are two runs of one walk, so
        // exact equality is the right assertion rather than a tolerance.
        if (partition.Estimate() != total)
        {
            throw new InvalidOperationException(
                $"the partition costed this scene at {partition.Estimate()} statements and the "
                + $"emitter produced {total}; the probe and the emission have drifted.");
        }

        return new CompiledScene
        {
            Scene = scene,
            Source = diagnostics.Source,
            Chunks = chunks,
            Primitives = [.. tables.Primitives],
            Materials = [.. tables.Materials],
            Shapes = [.. tables.Shapes],
            Instances = [.. instances],
            Nodes = [.. nodes],
            LeafShapes = [.. leafShapes],
            RootCount = partition.GroupOfRoot.Count,
            ShareFrom = shareFrom,
            Budget = budget,
        };
    }

    /// <summary>
    /// How many shared shapes the chunks already emitted came to, which is the number the next
    /// chunk's first shared shape answers to.
    /// </summary>
    /// <remarks>
    /// Counted off the partition rather than off the emitters, so that it can be known before the
    /// chunk is walked. The two agree by construction: a group is emitted as a shared body when
    /// and only when it is <see cref="ShapeGroup.Instanced"/>.
    /// </remarks>
    private static int SharedSoFar(IReadOnlyList<IReadOnlyList<ShapeGroup>> split, int done) =>
        split.Take(done).Sum(shapes => shapes.Count(shape => shape.Instanced));

    /// <summary>What each distinct shape turned out to be, for the console line and the refusal.</summary>
    /// <remarks>
    /// Taken from the group's own root rather than from a placement, because the shape's
    /// definition is what an author would edit to make it cheaper, and the peeled root is where
    /// the geometry actually is: an <c>object { }</c> wrapper carries a position and no cost.
    /// </remarks>
    private static ShapeReport[] Report(IReadOnlyList<ShapeGroup> shapes) =>
    [
        .. shapes.Select(shape => new ShapeReport(
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

            // One chunk, and never more. A sphere-traced scene is one distance function reached
            // from a march loop, so there is no per-shape body to split and nothing a second
            // program could hold.
            Chunks =
            [
                new CompiledChunk
                {
                    Geometry = emitter.Build(),

                    // Not costed. A sphere-traced program is a different shape of program, and a
                    // number that looked comparable with the span path's without being it would
                    // be worse than no number. Empty rather than wrong, which also means this
                    // backend reports no shapes to a refusal -- correctly, since it has none in
                    // the sense the rest of this file means.
                    ShapeReports = [],

                    ShapeCount = scene.Roots.Count,
                    NodeBase = 0,
                    NodeCount = 0,

                    // A distance field has no span lists. Reported as zero rather than left to
                    // mean something it cannot mean here.
                    WidestRoot = 0,
                },
            ],

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

            // Nor is it cut. A sphere-traced scene is one distance function, so there are no
            // separate roots for a cut to make.
            RootCount = scene.Roots.Count,

            // Nothing was shared, so there is no threshold that could have shared more and
            // nothing for a driver refusal to retry.
            ShareFrom = ShapePartition.ShareEverything,
            Budget = ShapeCost.Budget,
        };
    }
}
