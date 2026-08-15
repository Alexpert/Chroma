using System.Globalization;
using Chroma.Core.Compilation;
using Chroma.Core.Sdl.Source;

namespace Chroma.Core.Tests;

/// <summary>
/// Cutting a shape too large to emit into the operands of its own <c>union</c>.
/// </summary>
/// <remarks>
/// <para>
/// Roots are implicitly unioned and resolved one at a time, so cutting a <c>union</c> into one root
/// per operand needs no new machinery. What it needs is a reason and a limit, and both are what
/// these are about: a scene that fits must come out of it untouched, and a cut must not be made
/// where separate resolution would change the picture.
/// </para>
/// <para>
/// The failure to fear is the quiet one. A cut that dropped an operand, lost the union's own
/// transform or its material, or shared two shapes that were not the same, renders a wrong picture
/// rather than failing to compile, which is why the strongest test here is that every solid in
/// <c>cube.chroma</c> stands where it stood when the scene was compiled whole. See
/// documents/cutting-unions.md.
/// </para>
/// </remarks>
public sealed class RootSplittingTests
{
    /// <summary>
    /// A scene that fits is compiled exactly as it was before cutting existed.
    /// </summary>
    /// <remarks>
    /// The same bargain the budget and the chunker each struck before: the new rule may only bite
    /// where the old arrangement had nothing to offer. Asserted on the generated text, because that
    /// is what "unchanged" has to mean here — a union resolved as one list and a union resolved as
    /// nine differ in the picture only from inside two of the spheres at once, and would pass
    /// anything weaker.
    /// </remarks>
    [Fact]
    public void A_union_that_fits_is_never_cut()
    {
        CompiledScene scene = Compile(Spheres(9), ShapeCost.Budget);

        Assert.False(scene.WasCut);
        Assert.Equal(1, scene.RootCount);
        Assert.Equal(9, scene.WidestRoot);
        Assert.Equal(scene.Geometry, Compile(Spheres(9), int.MaxValue).Geometry);
    }

    /// <summary>
    /// A shape no program could hold is cut into the operands of its union.
    /// </summary>
    [Fact]
    public void A_shape_too_large_for_any_program_is_cut_apart()
    {
        CompiledScene scene = Compile(Spheres(9), budget: 10);

        Assert.True(scene.WasCut);
        Assert.Equal(9, scene.RootCount);

        // And the width came down with the cut, which is the half of it that the estimate does not
        // measure: nine spans of state per thread became one.
        Assert.Equal(1, scene.WidestRoot);
    }

    /// <summary>
    /// Cutting is what lets instancing see a repeated shape it could not see before.
    /// </summary>
    /// <remarks>
    /// The reason the cut is worth making at all. Twelve identical boxes inside one <c>union</c>
    /// are one shape with twelve leaves and can be shared with nothing; cut apart they are twelve
    /// appearances of one shape, and the program holds one body. That is why <c>cube.chroma</c>
    /// ends up smaller than the budget rather than merely spread across more programs.
    /// </remarks>
    [Fact]
    public void Cutting_lets_the_pieces_share_one_body()
    {
        CompiledScene scene = SceneLoader.Recompile(
            Compile(IdenticalBoxes(12), budget: 10), ShapePartition.ShareEverything, budget: 10);

        Assert.True(scene.WasCut);
        Assert.Equal(1, scene.ShapeCount);
        Assert.Equal(12, scene.InstanceCount);
    }

    /// <summary>
    /// Cutting a scene changes how a solid is reached and never where it is.
    /// </summary>
    /// <remarks>
    /// <c>ChunkingTests.Splitting_a_scene_never_moves_a_solid</c>, one level down, and on the scene
    /// that exercises it hardest: <c>cube.chroma</c> is cut twice over, and each round has to carry
    /// a <c>scale:</c> off a union onto every piece of it. Compiled whole at a budget nothing can
    /// exceed, against the real budget, and all eight thousand boxes compared through the
    /// scene-wide tables.
    /// </remarks>
    [Fact]
    public void Cutting_never_moves_a_solid()
    {
        SceneReader.AssertSameSolids(
            CompileScene("cube.chroma", int.MaxValue),
            CompileScene("cube.chroma", ShapeCost.Budget),
            "cut");
    }

    /// <summary>
    /// A cut carries the union's own transform and material onto every piece.
    /// </summary>
    /// <remarks>
    /// The two things a naive cut drops. A <c>union</c> is a solid like any other: it can be moved,
    /// and it can declare the material its operands inherit. Losing the first puts the pieces back
    /// at the origin; losing the second makes them the default grey, and neither shows up as an
    /// error.
    /// </remarks>
    [Fact]
    public void A_cut_carries_the_unions_transform_and_material()
    {
        const string body = """
            let red = material { color: [0.9, 0.1, 0.1] };

            union {
              sphere { radius: 1 }
              sphere { center: [4, 0, 0], radius: 1 }
              translate: [0, 5, 0]
              material: red
            }
            """;

        CompiledScene cut = Compile(body, budget: 10);
        Assert.True(cut.WasCut);

        SceneReader.AssertSameSolids(Compile(body, int.MaxValue), cut, "cut");

        // The inherited material survived as well as the position: one material besides the
        // default, worn by both pieces.
        Assert.Equal(Compile(body, int.MaxValue).MaterialCount, cut.MaterialCount);
    }

    /// <summary>
    /// A union of unions is cut over as many rounds as it takes.
    /// </summary>
    /// <remarks>
    /// One round exposes the inner unions as shapes of their own; only the round after that can
    /// see they are still too wide. <c>cube.chroma</c> needs exactly this, which is why the cut is
    /// a loop and not a pass.
    /// </remarks>
    [Fact]
    public void A_union_of_unions_is_cut_over_several_rounds()
    {
        string body = "union {\n"
            + string.Join("\n", Enumerable.Range(0, 4).Select(Group))
            + "\n}";

        CompiledScene scene = Compile(body, budget: 10);

        // Twelve leaves, and every one of them a root of its own: one round would have given four.
        Assert.Equal(12, scene.RootCount);
        Assert.Equal(1, scene.WidestRoot);

        static string Group(int i) =>
            $"union {{ sphere {{ center: [{i * 9}, 0, 0], radius: 1 }} "
            + $"sphere {{ center: [{(i * 9) + 3}, 0, 0], radius: 1 }} "
            + $"sphere {{ center: [{(i * 9) + 6}, 0, 0], radius: 1 }} }}";
    }

    /// <summary>
    /// Two overlapping transmissive operands are left in one union.
    /// </summary>
    /// <remarks>
    /// The one thing separate roots lose is that overlapping intervals stop merging, and for glass
    /// that is a lens-shaped seam where the two solids cross — the case
    /// <c>scenes/glass.chroma</c> exists in part to show has no seam. So the cut declines, and the
    /// shape stays whole even though it is over budget and the driver may well refuse it. A wrong
    /// picture is worse than a refusal.
    /// </remarks>
    [Fact]
    public void Two_overlapping_transmissive_operands_are_not_cut()
    {
        CompiledScene scene = Compile(Pair(transmissive: true, apart: 1.0), budget: 10);

        Assert.False(scene.WasCut);
        Assert.Equal(1, scene.RootCount);
    }

    /// <summary>
    /// The same two, standing apart, are cut.
    /// </summary>
    /// <remarks>
    /// Which is what makes the rule a test on the operands rather than "never cut a union holding
    /// glass". Two panes at opposite ends of a room have no intervals to merge, so separating them
    /// changes nothing at all.
    /// </remarks>
    [Fact]
    public void Transmissive_operands_that_stand_apart_are_cut()
    {
        CompiledScene scene = Compile(Pair(transmissive: true, apart: 8.0), budget: 10);

        Assert.True(scene.WasCut);
        Assert.Equal(2, scene.RootCount);
    }

    /// <summary>
    /// An opaque pair is cut however much it overlaps.
    /// </summary>
    /// <remarks>
    /// The nearer entry is the nearer entry whether the two intervals were merged or not, so an
    /// opaque overlap costs nothing from outside. What it does cost is the case
    /// documents/csg-raytracing.md already records for roots that were written separately: a ray
    /// starting inside both at once leaves at a surface interior to the union. Accepted knowingly,
    /// because refusing here would leave <c>cube.chroma</c> uncut for a case no scene renders from.
    /// </remarks>
    [Fact]
    public void An_opaque_pair_is_cut_however_much_it_overlaps()
    {
        CompiledScene scene = Compile(Pair(transmissive: false, apart: 1.0), budget: 10);

        Assert.True(scene.WasCut);
        Assert.Equal(2, scene.RootCount);
    }

    /// <summary>
    /// A shape with no seam to cut on is left exactly as it was.
    /// </summary>
    /// <remarks>
    /// A forty-segment <c>lathe</c> is one leaf, forty spans wide and past
    /// <see cref="ShapeCost.MaxSpans"/>, and there is nothing inside it to separate. The loop has
    /// to notice that and stop rather than spin, and the scene has to come out compiled: the width
    /// is a target for cutting and never a limit a scene can fail.
    /// </remarks>
    [Fact]
    public void A_shape_with_no_seam_to_cut_on_is_left_alone()
    {
        string points = string.Join(", ", Enumerable.Range(0, 40).Select(i => $"1, {i}"));
        CompiledScene scene = Compile($"lathe {{ points: [{points}] }}", budget: 10);

        Assert.False(scene.WasCut);
        Assert.Equal(40, scene.WidestRoot);
    }

    /// <summary>
    /// A scene over budget only in aggregate is chunked and not cut.
    /// </summary>
    /// <remarks>
    /// The rule that keeps the two mechanisms apart. Two hundred posts of which no single one is
    /// near the budget is what chunking is for, and chunking costs a second pass of the path
    /// tracer where cutting costs the coalescing between two operands. Cutting only ever reaches a
    /// shape that could not fit a program <i>on its own</i>, which no post here is.
    /// </remarks>
    [Fact]
    public void A_scene_over_budget_only_in_aggregate_is_chunked_and_not_cut()
    {
        CompiledScene palisade = CompileScene("palisade.chroma", ShapeCost.Budget);

        Assert.False(palisade.WasCut);
        Assert.True(palisade.Chunks.Count > 1, "palisade is meant to be the scene that is chunked");
    }

    // -----------------------------------------------------------------------------------------
    // Scenes.
    // -----------------------------------------------------------------------------------------

    /// <summary>A union of spheres far enough apart that their boxes do not meet.</summary>
    private static string Spheres(int count) =>
        "union {\n"
        + string.Join(
            "\n",
            Enumerable.Range(0, count).Select(i => $"  sphere {{ center: [{i * 3}, 0, 0], radius: 1 }}"))
        + "\n}";

    /// <summary>A union of boxes that are the same shape standing in different places.</summary>
    /// <remarks>
    /// The position is written <i>inside</i> the primitive, as <c>min:</c>/<c>max:</c>, because
    /// that is the idiom <see cref="ShapeCanonicalizer"/> has to normalise away for the pieces to
    /// be recognised as one shape, and the idiom <c>cube.chroma</c> is written in.
    /// </remarks>
    private static string IdenticalBoxes(int count) =>
        "union {\n"
        + string.Join(
            "\n",
            Enumerable.Range(0, count).Select(i =>
                $"  box {{ min: [{i * 3}, -1, -1], max: [{(i * 3) + 2}, 1, 1] }}"))
        + "\n}";

    /// <summary>Two spheres in one union, optionally glass, optionally overlapping.</summary>
    private static string Pair(bool transmissive, double apart)
    {
        string material = transmissive
            ? "material { transmission: 1, ior: 1.5 }"
            : "material { color: [0.8, 0.8, 0.8] }";

        return $"let m = {material};\n"
            + "union {\n"
            + "  sphere { radius: 1 }\n"
            + $"  sphere {{ center: [{apart.ToString("0.###", CultureInfo.InvariantCulture)}, 0, 0], "
            + "radius: 1 }\n"
            + "  material: m\n"
            + "}";
    }

    // -----------------------------------------------------------------------------------------
    // Compiling.
    // -----------------------------------------------------------------------------------------

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
