using Chroma.Core.Compilation;
using Chroma.Core.Sdl.Source;

namespace Chroma.Core.Tests;

/// <summary>
/// What a shape costs, and what the compiler does with the number.
/// </summary>
/// <remarks>
/// <para>
/// The driver caps a program at roughly 65,000 assembly instructions and never says how many it
/// counted, so the compiler estimates. Two things ride on the estimate: which shapes are shared
/// beyond what speed already wanted, and which shapes a refusal names. Both are wrong in ways
/// nobody would notice if the estimate were quietly wrong, which is what these are for.
/// </para>
/// <para>
/// The calibration is not here. What number of statements this driver will take is a
/// measurement, made by tools/measure-shape-cost.ps1 against a real GPU and recorded in
/// documents/gpu-backends.md. These test the parts that are not measurements: that the estimate
/// tracks what is actually emitted, that it moves the right way, and that the budget only ever
/// adds sharing to what the placement threshold already chose.
/// </para>
/// </remarks>
public sealed class ShapeCostTests
{
    /// <summary>
    /// The partition costs a scene at exactly what the emitter goes on to produce.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The seam. <c>GeometryEmitter.Probe</c> walks every root once to find out what it emits, and
    /// the partition decides what to share on the cost that walk reported; the emitter then walks
    /// it again for real. If the two ever costed the same shape differently, every decision above
    /// would be made about a scene that is not the one being built.
    /// </para>
    /// <para>
    /// <c>SceneCompiler</c> throws on a mismatch rather than carrying on, so this is a test that
    /// compiling does not throw. It is written out anyway, over every scene in the repository,
    /// because a guard nothing exercises is a guard nobody knows is there. The equality is exact:
    /// these are two runs of one walk, not two estimates of one thing.
    /// </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(RepositoryScenes))]
    public void What_the_partition_costs_is_what_the_emitter_emits(string name)
    {
        // Both partitions, because the two paths through the emitter -- a shared body written
        // once, a folded one written at each appearance -- are costed by different arithmetic.
        CompiledScene folded = CompileScene(name, ShapePartition.DefaultShareFrom, int.MaxValue);
        CompiledScene shared = CompileScene(name, ShapePartition.ShareEverything, int.MaxValue);

        Assert.True(folded.EstimatedCost > 0, $"{name} was costed at nothing");
        Assert.True(shared.EstimatedCost > 0, $"{name} was costed at nothing when shared");
    }

    [Fact]
    public void A_shape_with_more_in_it_costs_more()
    {
        // Every one of these is a loop the driver unrolls against a literal bound, which is the
        // one thing separating this estimate from a line count. If any of them stopped scaling,
        // the estimate would rank a chess piece beside a sphere.
        Assert.True(Cost(Lathe(24)) > Cost(Lathe(12)));
        Assert.True(Cost(Lathe(12)) > Cost(Lathe(6)));
        Assert.True(Cost(Prism(12)) > Cost(Prism(6)));
        Assert.True(Cost(Blob(8)) > Cost(Blob(3)));
        Assert.True(Cost(Sweep(10)) > Cost(Sweep(4)));
    }

    [Fact]
    public void A_tree_costs_more_than_the_leaf_it_is_built_from()
    {
        int one = Cost("sphere { radius: 1 }");
        int two = Cost("union { sphere { radius: 1 } box { min: [0, 0, 0], max: [1, 1, 1] } }");

        Assert.True(two > one, "a union of two solids came out no dearer than one of them");

        // A difference is an intersection with a complement, so it writes one more operator body
        // than a union over the same operands does.
        int difference = Cost(
            "difference { sphere { radius: 1 } box { min: [0, 0, 0], max: [1, 1, 1] } }");

        Assert.True(difference > two, "a difference came out no dearer than a union");
    }

    [Fact]
    public void Sharing_a_shape_is_what_stops_the_scene_growing_with_its_placements()
    {
        // The claim instancing was built on, as a number rather than as a shape count: twenty
        // pawns cost twenty pawns folded and one pawn shared.
        string scene = Repeat(20, i => $"sphere {{ radius: 0.5, translate: [{i}, 0, 0] }}");

        int folded = Compile(scene, ShapePartition.ShareEverything + 1000, int.MaxValue).EstimatedCost;
        int shared = Compile(scene, ShapePartition.ShareEverything, int.MaxValue).EstimatedCost;

        Assert.True(
            folded > shared * 15,
            $"twenty folded placements cost {folded} against {shared} shared, which is not the "
            + "difference instancing exists to make");
    }

    /// <summary>
    /// The budget sheds the expensive shape and leaves the cheap one folded.
    /// </summary>
    /// <remarks>
    /// The case the all-or-nothing threshold served worst. This scene repeats far too little for
    /// the placement rule to want a tree, and it is over budget anyway; before, its only answer
    /// was to share everything and lose the folded form on every shape in it. The greedy sheds
    /// the one that saves the most and stops as soon as it fits.
    /// </remarks>
    [Fact]
    public void Over_budget_the_dearest_repeated_shape_is_shared_and_the_rest_stay_folded()
    {
        string scene =
            Repeat(3, i => $"lathe {{ points: [{Outline(24)}], translate: [{i * 3}, 0, 0] }}")
            + Repeat(3, i => $"sphere {{ radius: 0.5, translate: [{i * 3}, 4, 0] }}");

        CompiledScene roomy = Compile(scene, ShapePartition.DefaultShareFrom, int.MaxValue);

        Assert.All(roomy.ShapeReports, shape => Assert.False(shape.Instanced));

        int lathe = Only(roomy, "lathe").Cost;
        int sphere = Only(roomy, "sphere").Cost;

        // Room for the lathe shared and the spheres folded, and no room for six folded shapes.
        CompiledScene tight = Compile(scene, ShapePartition.DefaultShareFrom, lathe + (3 * sphere));

        Assert.True(Only(tight, "lathe").Instanced, "the expensive shape was left folded");
        Assert.False(Only(tight, "sphere").Instanced, "the cheap shape was shared for no reason");
        Assert.True(tight.EstimatedCost <= lathe + (3 * sphere));
    }

    [Fact]
    public void A_budget_nothing_can_meet_shares_everything_it_can_and_stops()
    {
        // Greedy runs out of candidates rather than looping. Worth pinning: the driver is the
        // authority and will refuse this scene anyway, and it has to refuse it with a diagnostic
        // rather than with a hang.
        string scene = Repeat(4, i => $"lathe {{ points: [{Outline(24)}], translate: [{i * 3}, 0, 0] }}");

        CompiledScene compiled = Compile(scene, ShapePartition.DefaultShareFrom, 1);

        Assert.Equal(1, compiled.ShapeCount);
        Assert.True(compiled.ShapeReports[0].Instanced);
        Assert.True(compiled.EstimatedCost > 1, "the budget was met, which this scene cannot do");
    }

    /// <summary>
    /// A scene that fits is partitioned exactly as it was before the budget existed.
    /// </summary>
    /// <remarks>
    /// The budget composes with the placement threshold rather than replacing it: sharing only
    /// ever shrinks a program, so a budget can add to what speed already chose and never take any
    /// of it away. Every scene in the repository is well under budget, so every one of them must
    /// come out of the partition unchanged, and this is what says so.
    /// </remarks>
    [Theory]
    [MemberData(nameof(RepositoryScenes))]
    public void Under_budget_nothing_is_shared_that_speed_did_not_ask_for(string name)
    {
        CompiledScene budgeted = CompileScene(name, ShapePartition.DefaultShareFrom, ShapeCost.Budget);
        CompiledScene unlimited = CompileScene(name, ShapePartition.DefaultShareFrom, int.MaxValue);

        Assert.Equal(
            unlimited.ShapeReports.Select(shape => shape.Instanced),
            budgeted.ShapeReports.Select(shape => shape.Instanced));

        Assert.True(
            budgeted.EstimatedCost <= ShapeCost.Budget,
            $"{name} is estimated at {budgeted.EstimatedCost} against a budget of "
            + $"{ShapeCost.Budget}, so the repository no longer has room to spare");
    }

    [Fact]
    public void A_refusal_names_the_shape_that_costs_the_most_and_where_it_was_written()
    {
        string scene =
            "sphere { radius: 0.5, translate: [-4, 0, 0] }\n"
            + $"lathe {{ points: [{Outline(24)}], translate: [4, 0, 0] }}";

        CompiledScene compiled = Compile(scene, ShapePartition.DefaultShareFrom, int.MaxValue);

        ShapeReport dearest = compiled.ShapeReports.OrderByDescending(shape => shape.Total).First();

        Assert.Equal("lathe", dearest.Kind);

        // The line the lathe is written on, counting the camera the harness prepends.
        Assert.Contains(":3:", dearest.Locate(compiled.Source));
        Assert.Contains("lathe", dearest.Describe(compiled.Source));
        Assert.Contains("of the budget", dearest.Describe(compiled.Source));
    }

    [Fact]
    public void A_shape_a_loop_produced_is_named_by_the_loop_rather_than_by_its_body()
    {
        // Every solid a loop emits shares one origin, so pointing at that line and saying it
        // costs forty percent of the budget would point at a line that reads perfectly well.
        CompiledScene compiled = Compile(
            "for (let i = 0; i < 3; i = i + 1) "
            + $"{{ lathe {{ points: [{Outline(24)}], translate: [i * 3, 0, 0] }} }}",
            ShapePartition.DefaultShareFrom,
            int.MaxValue);

        ShapeReport shape = Assert.Single(compiled.ShapeReports);

        Assert.NotNull(shape.Generator);
        Assert.Equal(3, shape.Generator!.Count);
        Assert.Contains("this loop over 'i'", shape.Describe(compiled.Source));
    }

    // -----------------------------------------------------------------------------------------
    // Building scenes to weigh.
    // -----------------------------------------------------------------------------------------

    public static TheoryData<string> RepositoryScenes()
    {
        TheoryData<string> scenes = [];

        // Every scene the repository ships, found rather than listed: a scene added later should
        // be held to this without anyone remembering to add it here.
        //
        // Four are left out and each is meant to be. diagnostics-demo does not parse; cube is over
        // budget as written and is cut apart to fit, so it is not partitioned the same way with a
        // budget as without one -- see RootSplittingTests; palisade is over budget and is split,
        // which is what it exists to demonstrate -- see ChunkingTests. cube-4 is left out for its
        // cost alone: it is cube one level deeper, 160,000 boxes, and this would compile it four
        // times over.
        foreach (string path in Directory.EnumerateFiles(
            Path.Combine(RepositoryRoot(), "scenes"), "*.chroma"))
        {
            string name = Path.GetFileName(path);

            if (name is not ("diagnostics-demo.chroma" or "cube.chroma" or "palisade.chroma"
                or "cube-4.chroma"))
            {
                scenes.Add(name);
            }
        }

        return scenes;
    }

    /// <summary>
    /// A shape that cannot fit is known not to fit before the driver says so.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This was <c>cube.chroma</c> until <see cref="RootSplitter"/> existed, and it is worth saying
    /// why it is not any more. Eight thousand boxes in nested <c>union</c>s is one shape with eight
    /// thousand leaves and nothing to share, which is what made it the example; cutting the union
    /// apart turns it into four hundred appearances of one shape, and it fits with room to spare.
    /// See RootSplittingTests.
    /// </para>
    /// <para>
    /// What the estimate has to keep doing is warning about a shape nothing can help, so this is
    /// now an <c>intersection</c>: not a <c>union</c>, so there is no seam to cut on, and its
    /// operands overlap so that none of it is pruned away as empty. Sharing has nothing to offer
    /// it either, and the renderer must not spend a second near-ceiling compile finding that out.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_shape_that_cannot_fit_is_known_to_not_fit_before_the_driver_says_so()
    {
        string body = "intersection {\n"
            + Repeat(800, i => $"  sphere {{ center: [{Number(i * 0.002)}, 0, 0], radius: 1 }}")
            + "}";

        CompiledScene folded = Compile(body, ShapePartition.DefaultShareFrom, ShapeCost.Budget);

        Assert.True(
            folded.EstimatedCost > ShapeCost.Budget,
            $"the shape is estimated at {folded.EstimatedCost}, which now fits a budget of "
            + $"{ShapeCost.Budget}; either the model or the budget has moved a long way");

        // Uncut, because an intersection is not a union: the shape a refusal will name is still
        // the whole of it.
        Assert.False(folded.WasCut);

        CompiledScene shared = Compile(body, ShapePartition.ShareEverything, 1);

        Assert.Equal(folded.EstimatedCost, shared.EstimatedCost);

        ShapeReport shape = Assert.Single(folded.ShapeReports);
        Assert.Equal(800, shape.Leaves);
    }

    /// <summary>An outline of <paramref name="points"/> vertices, closed on the axis.</summary>
    private static string Outline(int points)
    {
        List<string> parts = ["0, 0"];

        for (int k = 1; k < points - 1; k++)
        {
            double y = 2.0 * k / (points - 1);
            double r = 0.3 + (0.2 * Math.Sin(3.0 * k));
            parts.Add(Number(r) + ", " + Number(y));
        }

        parts.Add("0, 2");
        return string.Join(", ", parts);
    }

    private static string Lathe(int points) => $"lathe {{ points: [{Outline(points)}] }}";

    private static string Prism(int points)
    {
        List<string> parts = [];

        for (int k = 0; k < points; k++)
        {
            double a = 2 * Math.PI * k / points;
            parts.Add(Number(Math.Cos(a)) + ", " + Number(Math.Sin(a)));
        }

        return $"prism {{ bottom: 0, top: 2, points: [{string.Join(", ", parts)}] }}";
    }

    private static string Blob(int components)
    {
        string body = "blob { threshold: 0.5,";

        for (int k = 0; k < components; k++)
        {
            body += $" blobSphere {{ center: [{Number(k * 0.4)}, 0, 0], radius: 1, strength: 1 }}";
        }

        return body + " }";
    }

    private static string Sweep(int spheres)
    {
        List<string> parts = [];

        for (int k = 0; k < spheres; k++)
        {
            parts.Add($"{Number(k * 0.4)}, {Number(0.5 + (0.3 * Math.Sin(k)))}, 0, 0.2");
        }

        return $"sphereSweep {{ spheres: [{string.Join(", ", parts)}] }}";
    }

    private static string Repeat(int count, Func<int, string> body) =>
        string.Concat(Enumerable.Range(0, count).Select(i => body(i) + "\n"));

    /// <summary>A number a scene file will accept, whatever the machine's culture writes.</summary>
    private static string Number(double value) =>
        value.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);

    // -----------------------------------------------------------------------------------------
    // Weighing them.
    // -----------------------------------------------------------------------------------------

    private static int Cost(string body) =>
        Compile(body, ShapePartition.DefaultShareFrom, int.MaxValue).EstimatedCost;

    private static ShapeReport Only(CompiledScene scene, string kind) =>
        Assert.Single(scene.ShapeReports.Where(shape => shape.Kind == kind));

    /// <summary>Compiles a scene body at a given threshold and budget.</summary>
    /// <remarks>
    /// Through <see cref="SceneLoader.Recompile"/>, which exists for exactly this and carries the
    /// original file with it, so a report can still name a line afterwards.
    /// </remarks>
    private static CompiledScene Compile(string body, int shareFrom, int budget) =>
        SceneLoader.Recompile(TestSource.CompileValid(body), shareFrom, budget);

    private static CompiledScene CompileScene(string name, int shareFrom, int budget)
    {
        string path = Path.Combine(RepositoryRoot(), "scenes", name);

        bool ok = SceneLoader.TryLoadCompiled(
            path, out CompiledScene? compiled, out IReadOnlyList<Diagnostic> diagnostics);

        Assert.True(
            ok, $"failed to compile {name}: " + string.Join("; ", diagnostics.Select(d => d.Message)));

        return SceneLoader.Recompile(compiled!, shareFrom, budget);
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
