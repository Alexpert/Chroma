using System.Numerics;
using Chroma.Core;
using Chroma.Core.Compilation;
using Chroma.Core.Sdl.Source;

namespace Chroma.Core.Tests;

/// <summary>
/// Recovering shape identity, and what it buys.
/// </summary>
/// <remarks>
/// The language has no instancing and is not getting any: every use of a <c>let</c> or a
/// <c>function</c> produces an independent solid, on purpose. So two roots being the same shape
/// is a fact the compiler works out rather than one the author states, and these are the tests
/// that it works it out for the right reasons — and, just as importantly, that it declines to
/// when the two are not actually the same.
/// </remarks>
public sealed class InstancingTests
{
    [Fact]
    public void Two_identical_solids_in_different_places_are_one_shape()
    {
        CompiledScene scene = TestSource.CompileShared(
            "sphere { radius: 0.5, translate: [-2, 0, 0] }\n"
            + "sphere { radius: 0.5, translate: [ 2, 0, 0] }");

        Assert.Equal(1, scene.ShapeCount);
        Assert.Equal(2, scene.InstanceCount);
        Assert.Equal(2, scene.PrimitiveCount is 1 ? 2 : scene.PrimitiveCount + 1);
    }

    [Fact]
    public void One_body_is_emitted_however_many_places_it_stands_in()
    {
        CompiledScene scene = TestSource.CompileShared(
            "for (let i = 0; i < 40; i = i + 1) { sphere { radius: 0.5, translate: [i, 0, 0] } }");

        Assert.Equal(1, scene.ShapeCount);
        Assert.Equal(40, scene.InstanceCount);

        // The whole claim, in one assertion: forty placements, one leaf record and one shape
        // function. What grew is a buffer, and a buffer is not what the driver counts.
        Assert.Equal(1, scene.PrimitiveCount);
        Assert.Equal(1, Matches(scene, @"void shape\d+\("));
    }

    [Fact]
    public void Solids_that_differ_in_anything_but_placement_are_different_shapes()
    {
        CompiledScene scene = TestSource.CompileShared(
            "sphere { radius: 0.5, translate: [-2, 0, 0] }\n"
            + "sphere { radius: 0.6, translate: [ 2, 0, 0] }");

        Assert.Equal(2, scene.ShapeCount);
        Assert.Equal(0, scene.InstanceCount);
    }

    [Fact]
    public void A_rotation_is_part_of_the_placement_like_a_translation()
    {
        CompiledScene scene = TestSource.CompileShared(
            "box { min: [-1, -0.2, -0.2], max: [1, 0.2, 0.2], translate: [0, 0, -3] }\n"
            + "box { min: [-1, -0.2, -0.2], max: [1, 0.2, 0.2], rotate: [0, 90, 0], translate: [0, 0, 3] }");

        Assert.Equal(1, scene.ShapeCount);
        Assert.Equal(2, scene.InstanceCount);
    }

    [Fact]
    public void Material_is_not_part_of_what_makes_two_shapes_the_same()
    {
        // The case the work exists for: an ivory pawn and an obsidian one are one shape wearing
        // two different things, not two shapes. Keeping material in the key would have doubled
        // the number of turned pieces a chess set emits, which is the entire budget.
        CompiledScene scene = TestSource.CompileShared(
            "sphere { translate: [-2, 0, 0], material: { color: [1, 0, 0] } }\n"
            + "sphere { translate: [ 2, 0, 0], material: { color: [0, 0, 1] } }");

        Assert.Equal(1, scene.ShapeCount);
        Assert.Equal(2, scene.InstanceCount);

        // Two appearances, two runs of one material each -- and the leaf names slot 0 for both,
        // because which of the two it wears is the instance's business.
        Assert.Equal(2, scene.MaterialCount);
        Assert.Equal(0f, scene.Primitives[1]);
    }

    [Fact]
    public void Appearances_wearing_the_same_materials_share_one_run()
    {
        CompiledScene scene = TestSource.CompileShared(
            "let jade = material { color: [0.2, 0.6, 0.4] };\n"
            + "sphere { translate: [-2, 0, 0], material: jade }\n"
            + "sphere { translate: [ 2, 0, 0], material: jade }");

        Assert.Equal(1, scene.ShapeCount);
        Assert.Equal(2, scene.InstanceCount);
        Assert.Equal(1, scene.MaterialCount);
    }

    [Fact]
    public void How_a_shapes_materials_repeat_is_part_of_what_it_is()
    {
        // Which material a leaf wears belongs to the appearance; how many distinct ones the leaves
        // share out between them belongs to the shape. Both of these are one box and one sphere in
        // the same places, so their geometry is identical -- but one wears a single material and
        // the other wears two, and a shape emitted for the first cannot serve the second: its
        // leaves would name a slot the appearance has no material for.
        CompiledScene scene = TestSource.CompileShared(
            "union { box { } sphere { translate: [0, 2, 0] } translate: [-4, 0, 0], "
            + "material: { color: [1, 0, 0] } }\n"
            + "union { box { material: { color: [1, 0, 0] } } "
            + "sphere { translate: [0, 2, 0], material: { color: [0, 0, 1] } } translate: [4, 0, 0] }");

        Assert.Equal(2, scene.ShapeCount);
        Assert.Equal(0, scene.InstanceCount);
    }

    [Fact]
    public void A_shape_that_stands_in_one_place_keeps_its_placement_folded_in()
    {
        // The promise made to every scene that has nothing to repeat: it pays nothing. No
        // instance table means no buffer, no uNodeCount, no CHROMA_INSTANCES, and leaves whose
        // matrices reach world space directly, exactly as before instancing existed.
        CompiledScene scene = TestSource.CompileShared("sphere { }\nbox { }\ncylinder { }");

        Assert.Equal(3, scene.ShapeCount);
        Assert.Equal(0, scene.InstanceCount);
        Assert.Empty(scene.Instances);
        Assert.Empty(scene.Nodes);
        Assert.DoesNotContain("uNodeCount", scene.Geometry);
    }

    [Fact]
    public void A_shape_holding_a_plane_is_never_shared()
    {
        // A half-space has no bounding box, and a BVH is built out of boxes. It keeps the
        // unguarded block it has always had.
        CompiledScene scene = TestSource.CompileShared(
            "plane { normal: [0, 1, 0], distance: 0 }\n"
            + "plane { normal: [0, 1, 0], distance: 0 }");

        Assert.Equal(2, scene.ShapeCount);
        Assert.Equal(0, scene.InstanceCount);
    }

    [Fact]
    public void A_mirrored_placement_is_not_shared()
    {
        // Excluded knowingly rather than by accident. A negative determinant reverses surface
        // orientation, which meets the entering/leaving rule and the normal flip, and no scene
        // in the repository exercises it. As a singleton it behaves exactly as it did.
        CompiledScene scene = TestSource.CompileShared(
            "cone { base: [0, 0, 0], cap: [0, 1, 0], baseRadius: 0.5, capRadius: 0.1, translate: [-2, 0, 0] }\n"
            + "cone { base: [0, 0, 0], cap: [0, 1, 0], baseRadius: 0.5, capRadius: 0.1, "
            + "scale: [-1, 1, 1], translate: [2, 0, 0] }");

        Assert.Equal(2, scene.ShapeCount);
        Assert.Equal(0, scene.InstanceCount);
    }

    [Fact]
    public void A_shared_shape_is_reached_through_a_loop_the_driver_cannot_unroll()
    {
        CompiledScene scene = TestSource.CompileShared(
            "sphere { translate: [-2, 0, 0] }\nsphere { translate: [2, 0, 0] }");

        // The mechanism, asserted rather than assumed: the bound is a uniform, and a uniform is
        // what makes the driver expand the body once instead of once per placement.
        Assert.Contains("while (node < uNodeCount)", scene.Geometry);
        Assert.Contains("switch (int(INSTANCE(slot).x))", scene.Geometry);
    }

    [Fact]
    public void A_shape_below_the_threshold_is_written_out_once_per_appearance()
    {
        // Recognising two roots as one shape and then deciding not to share them must not lose
        // either of them. Getting this wrong deletes geometry rather than slowing anything down,
        // and it deletes it silently -- the scene simply renders with a solid missing.
        CompiledScene scene = TestSource.CompileValid(
            "sphere { radius: 0.5, translate: [-2, 0, 0] }\n"
            + "sphere { radius: 0.5, translate: [ 2, 0, 0] }");

        Assert.Equal(0, scene.InstanceCount);
        Assert.Equal(2, scene.ShapeCount);
        Assert.Equal(2, scene.PrimitiveCount);
        Assert.Equal(2, Matches(scene, @"void shape\d+\("));
    }

    [Fact]
    public void A_folded_appearance_is_emitted_where_it_actually_stands()
    {
        // The bug a cornell render found. Two boxes of the same size are one shape whose
        // appearances differ only in position -- correctly -- and under the sharing threshold both
        // are written out rather than instanced. Written out from the *group's* tree they both
        // land on the first one, and the second box silently disappears from the scene.
        //
        // The position is given by min/max rather than by translate:, which is the whole point:
        // that is how cornell's walls are written and how the earlier test was not, and it is the
        // case the shape frame normalises away.
        CompiledScene scene = TestSource.CompileValid(
            "box { min: [-1, -1, -1], max: [1, 1, 1] }\n"
            + "box { min: [ 9, -1, -1], max: [11, 1, 1] }");

        Assert.Equal(0, scene.InstanceCount);
        Assert.Equal(2, scene.PrimitiveCount);

        // A leaf's matrix reaches its local space from the world, so the two boxes differ in
        // where it sends the origin. Equal translations mean one box is standing inside the other.
        Vector3 first = Translation(scene, 0);
        Vector3 second = Translation(scene, 1);

        Assert.True(
            Vector3.Distance(first, second) > 1f,
            $"both boxes were emitted at the same place: {first} and {second}");
    }

    [Fact]
    public void Instancing_switches_on_once_a_scene_has_enough_placements()
    {
        // Instancing costs a dependent buffer read per node where the folded form costs an `if`,
        // so it is switched on by the size of the scene rather than by the mere fact of a repeat.
        // Below the threshold nothing is shared; above it, everything shareable is.
        CompiledScene below = TestSource.CompileValid(
            $"for (let i = 0; i < {ShapePartition.DefaultShareFrom - 1}; i = i + 1) "
            + "{ sphere { radius: 0.4, translate: [i, 0, 0] } }");

        CompiledScene above = TestSource.CompileValid(
            $"for (let i = 0; i < {ShapePartition.DefaultShareFrom}; i = i + 1) "
            + "{ sphere { radius: 0.4, translate: [i, 0, 0] } }");

        Assert.Equal(0, below.InstanceCount);
        Assert.Equal(ShapePartition.DefaultShareFrom - 1, below.ShapeCount);

        Assert.Equal(ShapePartition.DefaultShareFrom, above.InstanceCount);
        Assert.Equal(1, above.ShapeCount);
    }

    [Fact]
    public void The_tree_holds_one_appearance_per_leaf_and_covers_them_all()
    {
        CompiledScene scene = TestSource.CompileShared(
            "for (let i = 0; i < 17; i = i + 1) { sphere { radius: 0.4, translate: [i, 0, 0] } }");

        int leaves = 0;
        var seen = new HashSet<int>();

        for (int node = 0; node < scene.NodeCount; node++)
        {
            int instance = (int)scene.Nodes[(node * GpuLayout.NodeStride) + 7];

            if (instance < 0)
            {
                continue;
            }

            leaves++;
            Assert.True(seen.Add(instance), $"instance {instance} appears in two leaves");
        }

        Assert.Equal(17, leaves);

        // A binary tree whose leaves hold one thing each: 17 leaves and 16 interior nodes, and
        // no arrangement of them can be a different count.
        Assert.Equal(33, scene.NodeCount);
    }

    [Fact]
    public void Every_escape_index_leaves_the_subtree_it_belongs_to()
    {
        // The one invariant a stackless walk rests on. An escape that points inside its own
        // subtree loops for ever; one that points short of it silently drops geometry, which is
        // the harder failure to see.
        CompiledScene scene = TestSource.CompileShared(
            "for (let i = 0; i < 30; i = i + 1) { box { translate: [i, 0, 0] } }");

        for (int node = 0; node < scene.NodeCount; node++)
        {
            int escape = (int)scene.Nodes[(node * GpuLayout.NodeStride) + 3];

            Assert.True(escape > node, $"node {node} escapes backwards to {escape}");
            Assert.True(escape <= scene.NodeCount, $"node {node} escapes past the end to {escape}");
        }
    }

    /// <summary>
    /// What instancing is worth on the scenes that motivated it, pinned so that a regression in
    /// canonicalisation shows up as a number rather than as a scene that quietly stops linking.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>chess-full</c> is the one that matters. It was kept in the repository <i>because</i> it
    /// did not compile — the artifact that said where the wall stood — and the shape count here
    /// is what moved it: thirty-two pieces and sixty-four squares reach the ray through ten
    /// bodies, where before every one of them was written into the driver's assembly on its own.
    /// </para>
    /// <para>
    /// <c>cornell</c> and <c>colonnade</c> instance nothing, and that is the other half of the
    /// bargain rather than a failure. They repeat four and seven placements, far under the count
    /// at which a tree beats a run of folded guards, so they are emitted exactly as they were and
    /// run exactly as fast. See <see cref="ShapePartition.DefaultShareFrom"/>.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("chess-full.chroma", 10, 96)]
    [InlineData("chess-half.chroma", 10, 80)]
    [InlineData("lattice.chroma", 8, 124)]
    [InlineData("colonnade.chroma", 8, 0)]
    [InlineData("cornell.chroma", 8, 0)]
    public void Repository_scenes_share_what_they_repeat(string name, int shapes, int instances)
    {
        CompiledScene scene = CompileScene(name);

        Assert.Equal(shapes, scene.ShapeCount);
        Assert.Equal(instances, scene.InstanceCount);

        // One shape body per distinct shape, not one per placement. This is the same claim from
        // the other side, and it is the one the driver's instruction count actually follows.
        Assert.Equal(shapes, Matches(scene, @"void shape\d+\("));
    }

    /// <summary>
    /// Instancing may change how geometry is reached. It may never change where it is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The test that matters, and the one whose absence let a cornell box lose its ceiling to a
    /// bug that every unit test above was blind to. Each scene is compiled twice — once as it
    /// ships, once with every repeat shared — and each leaf's position in the world is
    /// reconstructed from whichever tables that compilation produced. The two sets have to agree.
    /// </para>
    /// <para>
    /// It catches what the narrower tests cannot because it does not care <i>how</i> a leaf is
    /// reached. A shape emitted from the wrong tree, an instance matrix composed the wrong way
    /// round, a placement dropped, a slot pattern mismatched: all of them move a solid, and this
    /// notices a solid that moved.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("chess-full.chroma")]
    [InlineData("chess-half.chroma")]
    [InlineData("chess.chroma")]
    [InlineData("lattice.chroma")]
    [InlineData("colonnade.chroma")]
    [InlineData("cornell.chroma")]
    [InlineData("chamber.chroma")]
    [InlineData("glass.chroma")]
    [InlineData("translucency.chroma")]
    [InlineData("fog.chroma")]
    [InlineData("csg.chroma")]
    [InlineData("shapes.chroma")]
    [InlineData("sweeps.chroma")]
    [InlineData("primitives.chroma")]
    [InlineData("magnify.chroma")]
    public void Sharing_a_shape_never_moves_it(string name)
    {
        List<Vector3> shipped = LeafPositions(CompileScene(name, ShapePartition.DefaultShareFrom));
        List<Vector3> shared = LeafPositions(CompileScene(name, ShapePartition.ShareEverything));

        Assert.Equal(shipped.Count, shared.Count);

        // Compared as sets: sharing reorders leaves, because a shape is emitted once and its
        // appearances follow the tree rather than the file.
        foreach (Vector3 position in shipped)
        {
            Assert.True(
                shared.Any(other => Vector3.Distance(position, other) < 1e-3f),
                $"{name}: a solid at {position} when shapes are folded is nowhere near it when they "
                + "are shared");
        }
    }

    /// <summary>
    /// Where every leaf in a compiled scene sits, in world space, however it is reached.
    /// </summary>
    /// <remarks>
    /// A folded leaf's matrix already reaches the world. A shared leaf's reaches only its shape,
    /// and the instance supplies the rest. The two compose as <c>instance * leaf</c> and not the
    /// other way round: these are row-vector matrices, so the one applied first is on the left,
    /// and a point arrives in the world and has to reach the shape before it can reach the leaf.
    /// The result is then inverted, because a matrix into a local space is not a position.
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

            // A leaf of a shared shape is at as many places as its shape has appearances, and the
            // instance record says which are its own.
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

    /// <summary>The four rows of a matrix written at <paramref name="header"/> plus one texel.</summary>
    private static Matrix4x4 MatrixAt(IReadOnlyList<float> table, int header)
    {
        int m = header + 4;

        return new Matrix4x4(
            table[m], table[m + 1], table[m + 2], table[m + 3],
            table[m + 4], table[m + 5], table[m + 6], table[m + 7],
            table[m + 8], table[m + 9], table[m + 10], table[m + 11],
            table[m + 12], table[m + 13], table[m + 14], table[m + 15]);
    }

    /// <summary>Where the local origin of a solid ends up, given the matrix into its local space.</summary>
    private static Vector3 OriginOf(Matrix4x4 toLocal) =>
        Matrix4x4.Invert(toLocal, out Matrix4x4 toWorld) ? toWorld.Translation : Vector3.Zero;

    /// <summary>Compiles a scene from the repository's <c>scenes/</c> directory.</summary>
    private static CompiledScene CompileScene(
        string name,
        int shareFrom = ShapePartition.DefaultShareFrom)
    {
        string path = Path.Combine(RepositoryRoot(), "scenes", name);

        Assert.True(
            SceneLoader.TryLoad(path, out var loaded, out var parseDiagnostics),
            "failed to load " + name + ": "
            + string.Join("; ", parseDiagnostics.Select(d => d.Message)));

        var diagnostics = new DiagnosticBag(new SourceText(path, File.ReadAllText(path)));
        CompiledScene? compiled = SceneCompiler.Compile(
            loaded!, diagnostics, GeometryBackend.Spans, shareFrom);

        Assert.True(compiled is not null, "failed to compile " + name);
        return compiled!;
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

    /// <summary>Where a leaf's world-to-local matrix sends the origin.</summary>
    /// <remarks>
    /// The fourth row of the record's matrix, which is the translation under the row-vector
    /// convention the packer writes and <c>fetchMatrix</c> reads. Enough to tell two appearances
    /// of one shape apart without reconstructing the whole transform.
    /// </remarks>
    private static Vector3 Translation(CompiledScene scene, int leaf)
    {
        int row = (leaf * GpuLayout.PrimitiveStride) + (4 * 4);
        return new Vector3(scene.Primitives[row], scene.Primitives[row + 1], scene.Primitives[row + 2]);
    }

    private static int Matches(CompiledScene scene, string pattern) =>
        System.Text.RegularExpressions.Regex.Matches(scene.Geometry, pattern).Count;
}
