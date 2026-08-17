using System.Text.RegularExpressions;
using Chroma.Core.Compilation;
using Chroma.Core.Model;
using Chroma.Core.Model.Geometry;
using Chroma.Core.Model.Geometry.Primitives;
using Chroma.Core.Sdl.Source;

namespace Chroma.Core.Tests;

/// <summary>
/// The <c>heightField</c> primitive: where its samples come from, what the emitter uploads, and
/// what it costs.
/// </summary>
/// <remarks>
/// <para>
/// Unlike <c>mesh</c>, this needs no files on disk: a height field's data is in the scene, which
/// is what the whole first half of the iteration is about. So everything here goes through
/// <see cref="TestSource"/> directly and there is no temporary directory anywhere in the file.
/// </para>
/// <para>
/// Two of these carry more weight than the rest.
/// <c>Costs_the_same_however_fine_the_grid_is</c> is the claim the primitive rests on, and
/// <c>Tells_two_different_height_fields_apart</c> is the one failure it can produce that would
/// otherwise be silent.
/// </para>
/// </remarks>
public sealed class HeightFieldTests
{
    /// <summary>A function whose value at a sample says exactly where the sample was taken.</summary>
    private const string Ramp = "function ramp(x, z) { return x + z * 10; }\n";

    [Fact]
    public void Samples_a_scene_function_over_the_footprint()
    {
        HeightField field = FieldOf(Ramp + "heightField { height: ramp, resolution: 4 }");

        Assert.Equal(4, field.Cells);
        Assert.Equal(25, field.SampleCount);
        Assert.Equal(25, field.Heights.Count);

        // The two ends land exactly on the footprint's edges rather than at the end of an
        // accumulated sum, because that is where the walls are.
        // Row major with z outermost, so the index is j * (cells + 1) + i.
        Assert.Equal(-11f, field.Heights[0], 5);                       // x = -1, z = -1
        Assert.Equal(11f, field.Heights[^1], 5);                       // x = +1, z = +1
        Assert.Equal(-9f, field.Heights[4], 5);                        // x = +1, z = -1
        Assert.Equal(9f, field.Heights[20], 5);                        // x = -1, z = +1
    }

    [Fact]
    public void Samples_the_same_terrain_at_two_resolutions()
    {
        // The load-bearing half of the convention: the function is called with COORDINATES, not
        // indices. Raising the resolution refines the same landscape; index arguments would make
        // every resolution a different one, and 'resolution' would stop meaning "how finely".
        HeightField coarse = FieldOf(Ramp + "heightField { height: ramp, resolution: 2 }");
        HeightField fine = FieldOf(Ramp + "heightField { height: ramp, resolution: 4 }");

        for (int j = 0; j <= 2; j++)
        {
            for (int i = 0; i <= 2; i++)
            {
                Assert.Equal(
                    coarse.Heights[(j * 3) + i],
                    fine.Heights[(j * 2 * 5) + (i * 2)],
                    5);
            }
        }
    }

    [Fact]
    public void Reads_a_grid_the_scene_built_itself()
    {
        HeightField field = FieldOf(
            "heightField { heights: [[0, 1, 2], [3, 4, 5], [6, 7, 8]] }");

        Assert.Equal(2, field.Cells);
        Assert.Equal([0f, 1f, 2f, 3f, 4f, 5f, 6f, 7f, 8f], field.Heights);
    }

    [Fact]
    public void Calls_a_builtin_as_readily_as_a_scene_function()
    {
        // A built-in is a value like any other, so a field that takes a function should not care
        // which kind it was handed. It is also the shortest terrain anyone can write.
        HeightField field = FieldOf("heightField { height: perlin, resolution: 8 }");

        Assert.Equal(81, field.SampleCount);

        // perlin is zero at every whole coordinate, and the footprint's corners are whole.
        Assert.Equal(0f, field.Heights[0], 6);
    }

    [Fact]
    public void Requires_exactly_one_of_height_and_heights()
    {
        Assert.Contains(
            "'heightField' requires 'height' or 'heights'",
            OneError("heightField { resolution: 4 }"));

        Assert.Contains(
            "'heightField' takes 'height' or 'heights', not both",
            OneError(Ramp + "heightField { height: ramp, heights: [[0, 0], [0, 0]] }"));
    }

    [Fact]
    public void Refuses_a_height_that_is_not_a_function_of_two_numbers()
    {
        Assert.Equal(
            "field 'height' expects a function, found a number",
            OneError("heightField { height: 3 }"));

        Assert.Equal(
            "'ridge' takes 3 arguments; 'height' calls it with the x and z of each sample, "
            + "which is 2",
            OneError(
                "function ridge(x, y, z) { return x; }\n"
                + "heightField { height: ridge, resolution: 2 }"));
    }

    [Fact]
    public void Reports_a_function_that_does_not_return_a_number_once()
    {
        // 16,641 samples and one sentence. A diagnostic per sample would bury the one worth
        // reading under a screenful of the same mistake.
        string message = OneError(
            "function ridge(x, z) { return \"high\"; }\n"
            + "heightField { height: ridge }");

        Assert.Contains("'ridge' returned the string \"high\"", message);
        Assert.Contains("'height' has to return a number", message);
    }

    [Fact]
    public void Refuses_a_resolution_beyond_the_cap()
    {
        Assert.Equal(
            $"field 'resolution' expects a value between 1 and "
            + $"{GpuLayout.MaxHeightFieldResolution}, found 2048",
            OneError(Ramp + "heightField { height: ramp, resolution: 2048 }"));
    }

    [Fact]
    public void Refuses_resolution_beside_a_grid()
    {
        Assert.Contains(
            "'resolution' says how finely 'height' is sampled and means nothing beside 'heights'",
            OneError("heightField { heights: [[0, 0], [0, 0]], resolution: 8 }"));
    }

    [Fact]
    public void Refuses_a_grid_that_is_not_rectangular_or_not_square()
    {
        Assert.Equal(
            "field 'heights' expects rows of equal length; row 1 has 2 where row 0 has 3",
            OneError("heightField { heights: [[0, 0, 0], [0, 0], [0, 0, 0]] }"));

        Assert.Equal(
            "field 'heights' expects rows of numbers, and row 1 element 1 is the string \"a\"",
            OneError("heightField { heights: [[0, 0], [0, \"a\"]] }"));

        Assert.Equal(
            "field 'heights' has 2 rows of 3; the grid has to be square",
            OneError("heightField { heights: [[0, 0, 0], [0, 0, 0]] }"));

        Assert.Equal(
            "field 'heights' needs at least 2 rows of 2, which is one cell; found 1",
            OneError("heightField { heights: [[0, 0]] }"));
    }

    [Fact]
    public void Floors_the_solid_at_the_lowest_sample_unless_the_scene_says_otherwise()
    {
        // Zero would be the wrong default. perlin is signed, so a floor at zero silently cuts
        // away everything below sea level and the first terrain anyone writes renders in pieces.
        HeightField defaulted = FieldOf("heightField { heights: [[-2, 0], [0, 5]] }");

        // Strictly below, and by a hair. Level with the minimum leaves the solid zero-thickness
        // wherever the terrain reaches its own floor, which for a terrain with a flat bottom is
        // an area rather than a point, and a ray entering there is neither in nor out.
        Assert.True(defaulted.Base < -2f, $"base {defaulted.Base} is not below the lowest sample");
        Assert.Equal(-2f, defaulted.Base, 2);
        Assert.Equal(5f, defaulted.High);

        HeightField cut = FieldOf("heightField { heights: [[-2, 0], [0, 5]], base: 1 }");

        Assert.Equal(1f, cut.Base);

        // A floor above every sample is not a solid, and saying so is better than an inverted box.
        Assert.Contains(
            "which leaves no solid",
            OneError("heightField { heights: [[-2, 0], [0, 5]], base: 9 }"));
    }

    [Fact]
    public void Lays_the_shape_block_out_as_the_shader_reads_it()
    {
        CompiledScene scene = TestSource.CompileValid(
            "heightField { heights: [[0, 1, 2], [3, 4, 5], [6, 7, 8]], base: -1 }");

        float[] head = HeaderOf(scene, 0);
        float[] spec = TexelOf(scene, (int)ParamAOf(scene, 0) + 1);

        Assert.Equal(2f, head[0]);                 // cells
        Assert.Equal(0f, head[1]);                 // not smooth
        Assert.Equal((2 * 2) + 2, head[2]);        // maxSteps: two boundaries a cell, plus the exit

        Assert.Equal(2f, spec[0]);                 // the samples follow the two header texels
        Assert.Equal(-1f, spec[2]);                // base, which the scene wrote

        // The lid, and it sits STRICTLY above the tallest sample. Where the surface touches it a
        // ray entering there is on the boundary, and whether it counts as inside decides the
        // parity of the whole march. It is not a surface of the solid, so lifting it is free.
        Assert.True(spec[3] > 8f, $"the lid {spec[3]} is not above the tallest sample");
        Assert.Equal(8f, spec[3], 2);

        // paramB is the cell count, which is what the shading path needs to find a cell again.
        Assert.Equal(2f, ParamBOf(scene, 0));
    }

    [Fact]
    public void Packs_four_heights_to_a_texel()
    {
        // A height is one number, so a texel apiece would spend three lanes on nothing: at the
        // cap that is 16.8 MB against 4.2.
        CompiledScene scene = TestSource.CompileValid(
            "heightField { heights: [[0, 1, 2], [3, 4, 5], [6, 7, 8]] }");

        int at = (int)ParamAOf(scene, 0);

        Assert.Equal(2 + 3, scene.Shapes.Length / GpuLayout.ShapeStride);
        Assert.Equal([0f, 1f, 2f, 3f], TexelOf(scene, at + 2));
        Assert.Equal([4f, 5f, 6f, 7f], TexelOf(scene, at + 3));

        // The tail repeats the last sample rather than trailing zeros. Nothing reads past the
        // end, so the value is arbitrary and a repeat keeps a dump of the buffer readable.
        Assert.Equal([8f, 8f, 8f, 8f], TexelOf(scene, at + 4));
    }

    [Fact]
    public void Costs_the_same_however_fine_the_grid_is()
    {
        // The claim the whole primitive rests on. The march takes its bound from the shape buffer
        // rather than from a literal, so the driver compiles one step instead of one per cell,
        // and iteration 15 counts a loop bounded by a runtime value at a constant. Break this and
        // a landscape silently becomes the most expensive thing in any scene that holds one.
        Assert.True(
            TexelsOf(64) > TexelsOf(4) * 100,
            "the two grids must differ in size for this to mean anything");

        Assert.Equal(CostOfShape(4), CostOfShape(64));
    }

    [Fact]
    public void Tells_two_different_height_fields_apart()
    {
        // The one failure this primitive can produce that would be silent. Two roots are decided
        // to be one shape by comparing the GLSL they emit, and a height field's grid is not in
        // its GLSL: the body carries an offset into a buffer, and inside the probe that computes
        // the comparison every buffer starts empty, so both fields emit offset zero. Without the
        // signature they compare equal and the second is drawn as the first.
        CompiledScene scene = TestSource.CompileValid(
            Ramp
            + """
              function bowl(x, z) { return x * x + z * z; }

              heightField { height: ramp, resolution: 4 }
              heightField { height: bowl, resolution: 4, translate: [4, 0, 0] }
              """);

        Assert.NotEqual(ParamAOf(scene, 0), ParamAOf(scene, 1));

        string[] signatures =
        [
            .. Regex.Matches(scene.Geometry, "// heightField ([0-9a-f]+)")
                .Select(match => match.Groups[1].Value)
                .Distinct(),
        ];

        Assert.Equal(2, signatures.Length);
    }

    [Fact]
    public void Uploads_one_copy_of_a_field_two_leaves_share()
    {
        CompiledScene one = TestSource.CompileValid(
            Ramp + "heightField { height: ramp, resolution: 8 }");

        CompiledScene two = TestSource.CompileValid(
            Ramp
            + """
              heightField { height: ramp, resolution: 8 }
              heightField { height: ramp, resolution: 8, translate: [4, 0, 0] }
              """);

        Assert.Equal(one.Shapes.Length, two.Shapes.Length);
        Assert.Equal(ParamAOf(two, 0), ParamAOf(two, 1));
    }

    [Fact]
    public void Distinguishes_a_smooth_field_from_a_faceted_one()
    {
        // The flag is in the header, so the two are two blocks. That costs one extra copy of the
        // grid in the one scene that compares them side by side, and keeps the rule that a block
        // is decided by its content and nothing else.
        CompiledScene scene = TestSource.CompileValid(
            """
            heightField { heights: [[0, 1], [1, 0]] }
            heightField { heights: [[0, 1], [1, 0]], smooth: true, translate: [4, 0, 0] }
            """);

        Assert.Equal(0f, HeaderOf(scene, 0)[1]);
        Assert.Equal(1f, HeaderOf(scene, 1)[1]);
        Assert.NotEqual(ParamAOf(scene, 0), ParamAOf(scene, 1));
    }

    [Fact]
    public void Subtracts_from_a_height_field_like_any_other_solid()
    {
        // The point of the primitive being a solid rather than a surface: it has a well-defined
        // inside, so it stands in an operator and the bite taken out of it is lit from within.
        CompiledScene scene = TestSource.CompileValid(
            Ramp
            + """
              difference {
                heightField { height: ramp, resolution: 8, maxSpans: 3 }
                sphere { center: [0, 0, 0], radius: 0.5 }
              }
              """);

        // A difference is A intersect complement(B), and complementing a one-span sphere gives
        // two, so the sweep emits at most |A| + |B|.
        Assert.Equal(4, scene.WidestRoot);
    }

    [Fact]
    public void Is_refused_by_the_distance_field_backend()
    {
        // Not an oversight. A height field is a triangle mesh on a grid, so its exact distance is
        // the same nearest-point search and its sign is the same ray cast, and a ray cast is the
        // span backend.
        SceneLoader.TryCompile(
            "test.chroma",
            TestSource.Camera + Ramp + "heightField { height: ramp, resolution: 4 }",
            out CompiledScene? compiled,
            out IReadOnlyList<Diagnostic> diagnostics,
            GeometryBackend.DistanceField);

        Assert.Null(compiled);
        Assert.Contains(
            diagnostics,
            d => d.Message.Contains("'heightField' is not supported by --sdf"));
    }

    // --- Driving the loader ----------------------------------------------------------------

    private static HeightField FieldOf(string body)
    {
        Solid root = Assert.Single(TestSource.LoadValid(body).Roots);

        return Assert.IsType<HeightField>(root);
    }

    /// <summary>The single error a failing scene reports, asserting that there is exactly one.</summary>
    private static string OneError(string body)
    {
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) = TestSource.Load(body);

        Assert.Null(scene);

        Diagnostic diagnostic = Assert.Single(
            diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));

        return diagnostic.Message;
    }

    /// <summary>What one appearance of this field weighs, in the units of <see cref="ShapeCost"/>.</summary>
    private static int CostOfShape(int resolution) =>
        Assert.Single(Compiled(resolution).ShapeReports).Cost;

    /// <summary>How many texels this field occupies in the shape buffer.</summary>
    private static int TexelsOf(int resolution) =>
        Compiled(resolution).Shapes.Length / GpuLayout.ShapeStride;

    private static CompiledScene Compiled(int resolution) =>
        TestSource.CompileValid(Ramp + $"heightField {{ height: ramp, resolution: {resolution} }}");

    // --- Reading the tables ----------------------------------------------------------------

    private static float ParamAOf(CompiledScene scene, int primitive) =>
        scene.Primitives[(primitive * GpuLayout.PrimitiveStride) + 2];

    private static float ParamBOf(CompiledScene scene, int primitive) =>
        scene.Primitives[(primitive * GpuLayout.PrimitiveStride) + 3];

    private static float[] HeaderOf(CompiledScene scene, int primitive) =>
        TexelOf(scene, (int)ParamAOf(scene, primitive));

    private static float[] TexelOf(CompiledScene scene, int texel) =>
        scene.Shapes[(texel * GpuLayout.ShapeStride)..((texel + 1) * GpuLayout.ShapeStride)];
}
