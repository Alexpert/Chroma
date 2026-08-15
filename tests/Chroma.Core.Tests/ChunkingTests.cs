using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using Chroma.Core.Compilation;
using Chroma.Core.Sdl.Source;

namespace Chroma.Core.Tests;

/// <summary>
/// Splitting a scene into several programs, and everything that must not change when it happens.
/// </summary>
/// <remarks>
/// <para>
/// A scene whose geometry will not fit one program is compiled as several, one per chunk of
/// shapes, and traced by running each in turn. The split is in the code only: primitives,
/// materials and instances stay one table each, indexed the same way whichever chunk produced the
/// hit, and only the BVH is per chunk.
/// </para>
/// <para>
/// Which makes the dangerous failure a silent one. Every index that crosses the seam — a leaf's
/// number, a shape's case label, an instance's slot, a node's base — is a literal in generated
/// code or a field in a shared table, and getting one wrong renders the wrong solid in the wrong
/// place rather than failing to compile. These are about the indices.
/// </para>
/// </remarks>
public sealed class ChunkingTests
{
    /// <summary>
    /// Every scene in the repository is still exactly one program.
    /// </summary>
    /// <remarks>
    /// The claim that chunking cannot have affected anything that works today. It is the same
    /// bargain the budget struck in the phase before: the new rule may only bite where the old
    /// arrangement had nothing to offer, and no scene here is in that position.
    /// </remarks>
    [Theory]
    [MemberData(nameof(RepositoryScenes))]
    public void A_scene_that_fits_is_one_program(string name)
    {
        CompiledScene scene = CompileScene(name, ShapeCost.Budget);

        CompiledChunk only = Assert.Single(scene.Chunks);
        Assert.Equal(0, only.NodeBase);

        // And the convenience that reads "the" geometry still answers, which is what the whole
        // megakernel path goes through.
        Assert.NotEmpty(scene.Geometry);
    }

    [Fact]
    public void A_scene_too_large_for_one_program_is_split_into_several()
    {
        CompiledScene whole = Compile(Turnery(24), int.MaxValue);
        Assert.Single(whole.Chunks);

        int budget = whole.EstimatedCost / 4;
        CompiledScene split = Compile(Turnery(24), budget);

        Assert.True(
            split.Chunks.Count > 1,
            $"a scene costed at {whole.EstimatedCost} was not split by a budget of {budget}");

        // Splitting moves no geometry between programs and adds none, so the scene weighs what it
        // weighed. What changed is how many programs carry it.
        Assert.Equal(whole.EstimatedCost, split.EstimatedCost);
        Assert.Equal(whole.ShapeCount, split.ShapeCount);
    }

    /// <summary>
    /// No chunk is over budget, unless it is one shape that could never fit alone.
    /// </summary>
    /// <remarks>
    /// The single-shape case is not a bug being tolerated. A chunk cuts between shapes, so a shape
    /// dearer than the whole budget has nowhere to go; giving it a chunk of its own and letting the
    /// driver refuse it is what keeps <c>cube.chroma</c>'s diagnostic working, and a chunker that
    /// quietly split it instead would trade a good message for a driver error.
    /// </remarks>
    [Fact]
    public void Every_chunk_fits_the_budget_or_is_one_shape_that_cannot()
    {
        CompiledScene scene = Compile(Turnery(24), Compile(Turnery(24), int.MaxValue).EstimatedCost / 5);

        foreach (CompiledChunk chunk in scene.Chunks)
        {
            Assert.True(
                chunk.EstimatedCost <= scene.Budget || chunk.ShapeCount == 1,
                $"a chunk of {chunk.ShapeCount} shapes came to {chunk.EstimatedCost} "
                + $"against a budget of {scene.Budget}");
        }
    }

    /// <summary>
    /// The chunks tile the scene: every shape once, every node once, every instance once.
    /// </summary>
    [Fact]
    public void The_chunks_partition_the_scene()
    {
        CompiledScene scene = Split();

        Assert.Equal(scene.ShapeCount, scene.Chunks.Sum(chunk => chunk.ShapeCount));

        // The node table is the one thing that stays one table while being carved up, so its
        // slices must meet end to end. A gap would leave nodes nothing reads; an overlap would
        // have one chunk walk another's tree and call a case label it does not have.
        int expected = 0;

        foreach (CompiledChunk chunk in scene.Chunks)
        {
            Assert.Equal(expected, chunk.NodeBase);
            expected += chunk.NodeCount;
        }

        Assert.Equal(scene.NodeCount, expected);
    }

    /// <summary>
    /// Shape ids run across the whole scene, and each chunk answers to its own.
    /// </summary>
    /// <remarks>
    /// Read out of the generated <c>switch</c>, because that is where being wrong would show: the
    /// id travels in an instance record, the record is in a scene-wide table, and a chunk that
    /// numbered its shapes from zero would answer another chunk's instances with its own geometry.
    /// The symptom would be a picture, not an error.
    /// </remarks>
    [Fact]
    public void Shape_ids_are_scene_wide_and_no_two_chunks_claim_one()
    {
        CompiledScene scene = Split();

        // Or the assertions below are about nothing. A scene of singletons has no instance table
        // and no case labels, and would pass this without any of it being true.
        Assert.True(scene.InstanceCount > 0, "the scene under test shares nothing");

        List<int> labels =
        [
            .. scene.Chunks.SelectMany(chunk =>
                Regex.Matches(chunk.Geometry, @"^\s*case (\d+):", RegexOptions.Multiline)
                    .Select(match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture))),
        ];

        Assert.Equal(labels.Count, labels.Distinct().Count());
        Assert.Equal([.. Enumerable.Range(0, labels.Count)], [.. labels.Order()]);

        // And every id an instance actually carries is one somebody handles.
        for (int instance = 0; instance < scene.InstanceCount; instance++)
        {
            int shape = (int)scene.Instances[instance * GpuLayout.InstanceStride];
            Assert.Contains(shape, labels);
        }
    }

    /// <summary>
    /// A chunk past the first reads its own slice of the node table.
    /// </summary>
    /// <remarks>
    /// The base is a literal rather than a uniform so that the first chunk — the only one a scene
    /// usually has — emits the text it always emitted. Which means the second chunk's base is
    /// visible in its source, and worth asserting: without it every chunk would walk the first
    /// chunk's tree.
    /// </remarks>
    [Fact]
    public void A_chunk_walks_the_tree_that_belongs_to_it()
    {
        CompiledScene scene = Split();

        foreach (CompiledChunk chunk in scene.Chunks.Where(chunk => chunk.NodeCount > 0))
        {
            string expected = chunk.NodeBase == 0 ? "NODE(node * NODE_TEXELS)" : $"({chunk.NodeBase} + node)";
            Assert.Contains(expected, chunk.Geometry);
        }
    }

    /// <summary>
    /// Splitting a scene changes how a solid is reached and never where it is.
    /// </summary>
    /// <remarks>
    /// <c>InstancingTests.Sharing_a_shape_never_moves_it</c>, one level up. That test caught a
    /// cornell box losing its ceiling and an instance matrix composed the wrong way round, and its
    /// lesson was to re-run it after any change to how geometry is reached rather than only after a
    /// change to what is emitted. Chunking is such a change.
    /// </remarks>
    [Fact]
    public void Splitting_a_scene_never_moves_a_solid()
    {
        List<Vector3> whole = LeafPositions(Compile(Turnery(16), int.MaxValue));
        List<Vector3> split = LeafPositions(Split(16));

        Assert.Equal(whole.Count, split.Count);

        // As sets: the chunker reorders nothing within a chunk, but a shape's appearances follow
        // its own chunk's tree rather than the scene's.
        foreach (Vector3 position in whole)
        {
            Assert.True(
                split.Any(other => Vector3.Distance(position, other) < 1e-3f),
                $"a solid at {position} in one program is nowhere near it when the scene is split");
        }
    }

    /// <summary>
    /// The one scene chunking cannot help is still refused, and still names what is in it.
    /// </summary>
    /// <remarks>
    /// Eight thousand boxes in a single <c>union</c> is one shape with eight thousand leaves, not
    /// eight thousand shapes. A chunk cuts between shapes, so there is nothing here to cut, and the
    /// right outcome is the diagnostic the phase before built rather than a split that cannot help.
    /// </remarks>
    [Fact]
    public void The_scene_no_split_can_help_is_left_whole_and_named()
    {
        CompiledScene cube = CompileScene("cube.chroma", ShapeCost.Budget);

        CompiledChunk chunk = Assert.Single(cube.Chunks);
        Assert.True(
            chunk.EstimatedCost > ShapeCost.Budget,
            "cube.chroma is meant to be the scene that does not fit");

        ShapeReport shape = Assert.Single(chunk.ShapeReports);
        Assert.Equal(8000, shape.Leaves);
        Assert.Contains("cube.chroma", shape.Locate(cube.Source));
    }

    /// <summary>
    /// The scene wavefront rendering exists for is split, and every piece of it fits.
    /// </summary>
    /// <remarks>
    /// Two hundred hexagonal posts, all different sizes. As one program the driver refuses it with
    /// <c>error C5041: cannot locate suitable resource to bind variable</c> — the register ceiling
    /// rather than the instruction one — so unlike every other scene here it has no megakernel at
    /// all, and what this asserts is that the compiler noticed and did something about it.
    /// </remarks>
    [Fact]
    public void The_scene_with_no_megakernel_is_split_and_every_piece_fits()
    {
        CompiledScene palisade = CompileScene("palisade.chroma", ShapeCost.Budget);

        Assert.True(
            palisade.EstimatedCost > ShapeCost.Budget,
            "palisade.chroma is meant to be the scene that does not fit one program");

        Assert.True(palisade.Chunks.Count > 1, "it was not split");

        foreach (CompiledChunk chunk in palisade.Chunks)
        {
            Assert.True(
                chunk.EstimatedCost <= ShapeCost.Budget,
                $"a chunk came to {chunk.EstimatedCost} against a budget of {ShapeCost.Budget}");
        }

        // And splitting it moved nothing: the same posts stand in the same places as they would
        // have if one program could have held them.
        List<Vector3> split = LeafPositions(palisade);
        List<Vector3> whole = LeafPositions(CompileScene("palisade.chroma", int.MaxValue));

        Assert.Equal(whole.Count, split.Count);

        foreach (Vector3 position in whole)
        {
            Assert.True(
                split.Any(other => Vector3.Distance(position, other) < 1e-3f),
                $"a post at {position} moved when the scene was split");
        }
    }

    // -----------------------------------------------------------------------------------------
    // Scenes.
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// A rack of turned pieces that are all different from each other.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Different by <b>size</b> and never by position. Two solids are the same shape when they emit
    /// the same GLSL, and a position is normalised out of that comparison on purpose — so a rack
    /// distinguished by where its pieces stand would be one shape repeated, share into nothing, and
    /// measure the opposite of what is wanted here.
    /// </para>
    /// <para>
    /// Each piece then stands in <paramref name="copies"/> places, which is what makes the scene
    /// exercise the indices that matter. Distinct singletons would leave the instance table empty,
    /// and a test of scene-wide shape ids over a scene with no shape ids passes without asking
    /// anything. Four copies of a dozen pieces is comfortably past the sharing threshold, so every
    /// shape here is reached through the buffer.
    /// </para>
    /// </remarks>
    private static string Turnery(int pieces, int copies = 4)
    {
        string scene = string.Empty;

        for (int piece = 0; piece < pieces; piece++)
        {
            List<string> outline = ["0, 0"];

            for (int k = 1; k < 11; k++)
            {
                double y = 2.0 * k / 10;
                double r = 0.3 + (0.2 * Math.Sin(3.0 * k)) + (0.01 * piece);
                outline.Add(Number(r) + ", " + Number(y));
            }

            outline.Add("0, 2");

            for (int copy = 0; copy < copies; copy++)
            {
                scene += $"lathe {{ points: [{string.Join(", ", outline)}], "
                    + $"translate: [{Number(piece * 1.5)}, 0, {Number(copy * 3.0)}] }}\n";
            }
        }

        return scene;
    }

    private static string Number(double value) =>
        value.ToString("0.######", CultureInfo.InvariantCulture);

    /// <summary>A turnery split into several programs, at a budget a quarter of what it needs.</summary>
    private static CompiledScene Split(int pieces = 24) =>
        Compile(Turnery(pieces), Compile(Turnery(pieces), int.MaxValue).EstimatedCost / 4);

    public static TheoryData<string> RepositoryScenes()
    {
        TheoryData<string> scenes = [];

        foreach (string path in Directory.EnumerateFiles(Path.Combine(RepositoryRoot(), "scenes"), "*.chroma"))
        {
            string name = Path.GetFileName(path);

            // Three scenes are excluded and each is excluded on purpose. diagnostics-demo does not
            // parse; cube does not fit and cannot be split; palisade does not fit and is split,
            // which is what it exists to be. All three have tests of their own.
            if (name is "diagnostics-demo.chroma" or "cube.chroma" or "palisade.chroma")
            {
                continue;
            }

            scenes.Add(name);
        }

        return scenes;
    }

    // -----------------------------------------------------------------------------------------
    // Reading a compiled scene back.
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// Where every leaf sits in world space, however it is reached.
    /// </summary>
    /// <remarks>
    /// Chunk-agnostic by construction, and that is the point of it: it reads the scene-wide tables
    /// and the scene-wide shape ids, which are exactly the things a chunker could get wrong.
    /// </remarks>
    private static List<Vector3> LeafPositions(CompiledScene scene)
    {
        List<Vector3> positions = [];

        for (int leaf = 0; leaf < scene.PrimitiveCount; leaf++)
        {
            Matrix4x4 toLocal = MatrixAt(scene.Primitives, leaf * GpuLayout.PrimitiveStride);
            int shape = scene.LeafShapes[leaf];

            if (shape < 0)
            {
                positions.Add(OriginOf(toLocal));
                continue;
            }

            for (int instance = 0; instance < scene.InstanceCount; instance++)
            {
                int slot = instance * GpuLayout.InstanceStride;

                if ((int)scene.Instances[slot] != shape)
                {
                    continue;
                }

                positions.Add(OriginOf(MatrixAt(scene.Instances, slot) * toLocal));
            }
        }

        return positions;
    }

    private static Matrix4x4 MatrixAt(IReadOnlyList<float> table, int header)
    {
        int m = header + 4;

        return new Matrix4x4(
            table[m], table[m + 1], table[m + 2], table[m + 3],
            table[m + 4], table[m + 5], table[m + 6], table[m + 7],
            table[m + 8], table[m + 9], table[m + 10], table[m + 11],
            table[m + 12], table[m + 13], table[m + 14], table[m + 15]);
    }

    private static Vector3 OriginOf(Matrix4x4 toLocal) =>
        Matrix4x4.Invert(toLocal, out Matrix4x4 toWorld) ? toWorld.Translation : Vector3.Zero;

    private static CompiledScene Compile(string body, int budget) =>
        SceneLoader.Recompile(TestSource.CompileValid(body), ShapePartition.DefaultShareFrom, budget);

    private static CompiledScene CompileScene(string name, int budget)
    {
        string path = Path.Combine(RepositoryRoot(), "scenes", name);

        bool ok = SceneLoader.TryLoadCompiled(
            path, out CompiledScene? compiled, out IReadOnlyList<Diagnostic> diagnostics);

        Assert.True(
            ok, $"failed to compile {name}: " + string.Join("; ", diagnostics.Select(d => d.Message)));

        return SceneLoader.Recompile(compiled!, ShapePartition.DefaultShareFrom, budget);
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "scenes")))
        {
            directory = directory.Parent;
        }

        Assert.True(directory is not null, "could not find the repository root from the test binary");
        return directory!.FullName;
    }
}
