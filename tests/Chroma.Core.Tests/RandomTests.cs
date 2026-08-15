using Chroma.Core.Model;
using Chroma.Core.Model.Geometry.Primitives;
using Chroma.Core.Sdl.Source;

namespace Chroma.Core.Tests;

/// <summary>
/// <c>random</c> and <c>perlin</c>: the language's first built-in functions, and the scene
/// seed they are functions of.
/// </summary>
/// <remarks>
/// Everything here is observed through the scene model, because that is where the numbers end
/// up: a draw happens while the scene is being built, its result is an ordinary number in a
/// field, and nothing downstream can tell it from one that was typed.
/// </remarks>
public sealed class RandomTests
{
    /// <summary>The values of a list of expressions, read back as sphere centres.</summary>
    /// <remarks>
    /// A centre rather than a radius, because a centre has no constraint on it and takes a
    /// negative number, which <c>perlin</c> produces half the time.
    /// </remarks>
    private static double[] Values(string prelude, params string[] expressions)
    {
        string body = string.Join(
            "\n", expressions.Select(e => $"sphere {{ center: [{e}, 0, 0] }}"));

        Scene scene = TestSource.LoadValid(prelude + "\n" + body);

        return [.. scene.Roots.Select(r => (double)Assert.IsType<Sphere>(r).Center.X)];
    }

    private static double Value(string prelude, string expression) =>
        Values(prelude, expression)[0];

    [Fact]
    public void Draws_a_number_in_the_unit_interval()
    {
        double[] drawn = Values(
            "render { seed: 7 }",
            Enumerable.Range(0, 64).Select(i => $"random({i})").ToArray());

        Assert.All(drawn, value => Assert.InRange(value, 0.0, 0.9999999));

        // A hash that answered the same number every time would satisfy the range and nothing
        // else, which is the failure worth naming.
        Assert.True(drawn.Distinct().Count() > 60);
    }

    [Fact]
    public void Is_a_function_of_its_argument_and_not_a_stream()
    {
        // The property the whole design rests on: two calls with the same argument give the
        // same number, wherever in the file they are written and in whatever order the
        // evaluator happens to reach them. A stream would give two different numbers here.
        double[] drawn = Values("render { seed: 3 }", "random(5)", "random(9)", "random(5)");

        Assert.Equal(drawn[0], drawn[2]);
        Assert.NotEqual(drawn[0], drawn[1]);
    }

    [Fact]
    public void Draws_the_same_numbers_on_every_load_of_the_same_text()
    {
        // The manual's -Check compares 38 rendered images byte for byte, and a scene that
        // loads differently twice retires that and every other byte-identity sweep with it.
        const string prelude = "render { seed: 12 }";

        Assert.Equal(
            Values(prelude, "random(1)", "random(2)", "perlin(0.5, 1.5)"),
            Values(prelude, "random(1)", "random(2)", "perlin(0.5, 1.5)"));
    }

    [Fact]
    public void Draws_a_different_arrangement_for_a_different_seed()
    {
        Assert.NotEqual(
            Value("render { seed: 1 }", "random(0)"),
            Value("render { seed: 2 }", "random(0)"));
    }

    [Fact]
    public void Takes_a_fixed_default_seed_when_the_scene_names_none()
    {
        // Never a clock and never a process id: a scene that looks different every time it is
        // opened cannot be reviewed.
        Assert.Equal(
            Value($"render {{ seed: {RenderSettings.Default.Seed} }}", "random(4)"),
            Value(string.Empty, "random(4)"));
    }

    [Fact]
    public void Reads_the_seed_back_onto_the_scene()
    {
        Scene scene = TestSource.LoadValid("render { seed: 41 }\nsphere { }");

        Assert.Equal(41, scene.Render.Seed);
    }

    [Fact]
    public void Treats_the_two_zeros_as_one_argument()
    {
        // -0.0 and 0.0 compare equal and have different bit patterns, so hashing the bits
        // without collapsing them would make 'random(-i)' and 'random(i)' differ at i = 0.
        double[] drawn = Values("render { seed: 5 }", "random(0)", "random(-0)");

        Assert.Equal(drawn[0], drawn[1]);
    }

    [Fact]
    public void Composes_into_a_range_with_the_arithmetic_the_language_has()
    {
        // The reason there is one form rather than a family of them: 'lo + random(i) * (hi -
        // lo)' is the range, written in the language rather than built into the function.
        double[] drawn = Values(
            "render { seed: 2 }",
            Enumerable.Range(0, 32).Select(i => $"2 + random({i}) * 3").ToArray());

        Assert.All(drawn, value => Assert.InRange(value, 2.0, 5.0));
    }

    [Fact]
    public void Draws_coherent_noise_from_perlin()
    {
        // The one property 'random' cannot produce at any price: neighbouring inputs give
        // neighbouring outputs, which is the whole difference between scattering a hundred
        // posts and growing a landscape.
        double[] drawn = Values(
            "render { seed: 8 }", "perlin(4, 4)", "perlin(4.01, 4)", "perlin(37.3, 91.7)");

        Assert.True(Math.Abs(drawn[0] - drawn[1]) < 0.1);
        Assert.NotEqual(drawn[0], drawn[2]);
    }

    [Fact]
    public void Keeps_one_octave_of_perlin_inside_its_stated_range()
    {
        double[] drawn = Values(
            "render { seed: 6 }",
            Enumerable.Range(0, 48).Select(i => $"perlin({i} * 0.37, {i} * 0.11)").ToArray());

        Assert.All(drawn, value => Assert.InRange(value, -1.0, 1.0));

        // A single octave that never left the middle of its range would be a fade curve with
        // the noise missing, and the range assertion above would not notice.
        Assert.True(drawn.Any(v => v > 0.2) && drawn.Any(v => v < -0.2));
    }

    [Fact]
    public void Answers_zero_at_a_lattice_corner()
    {
        // Perlin noise is zero at every integer point by construction: the offset to each
        // corner is zero or one, so three of the four dot products vanish and the fourth is
        // weighted out. Worth pinning, since it is what makes a scene that samples on whole
        // coordinates get a flat field and wonder why.
        Assert.Equal(0.0, Value("render { seed: 9 }", "perlin(3, 5)"), 12);
    }

    [Fact]
    public void Reports_a_built_in_called_with_the_wrong_number_of_arguments()
    {
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) =
            TestSource.Load("sphere { radius: random(1, 2) }");

        Assert.Null(scene);
        Assert.Contains(diagnostics, d => d.Message.Contains("'random' takes 1 argument, found 2"));
    }

    [Fact]
    public void Reports_a_built_in_argument_that_is_not_a_number()
    {
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) =
            TestSource.Load("sphere { radius: random(true) }");

        Assert.Null(scene);
        Assert.Contains(diagnostics, d => d.Message.Contains("'i' of 'random' is a number"));
    }

    [Theory]
    [InlineData("function random(i) { return 1; }")]
    [InlineData("let perlin = 3;")]
    public void Reports_a_declaration_that_collides_with_a_built_in(string declaration)
    {
        // Nothing shadows, so this is an error rather than an override. It has to be reported
        // as a collision with a built-in: the frame it collides with is not in the file, and
        // "already defined" would send a reader looking for a declaration that is not there.
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) =
            TestSource.Load($"{declaration}\nsphere {{ }}");

        Assert.Null(scene);
        Assert.Contains(diagnostics, d => d.Message.Contains("is a built-in function of the language"));
    }

    [Fact]
    public void Reports_a_parameter_that_collides_with_a_built_in()
    {
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) =
            TestSource.Load("function f(random) { return 1; }\nsphere { }");

        Assert.Null(scene);
        Assert.Contains(diagnostics, d => d.Message.Contains("is a built-in function of the language"));
    }

    [Theory]
    [InlineData("random = 3;")]
    [InlineData("random++")]
    public void Reports_an_assignment_to_a_built_in(string statement)
    {
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) =
            TestSource.Load($"{statement}\nsphere {{ }}");

        Assert.Null(scene);
        Assert.Contains(diagnostics, d => d.Message.Contains("nothing assigns to one"));
    }

    [Fact]
    public void Refuses_a_seed_that_is_not_written_as_a_plain_number()
    {
        // The seed is read out of the text before anything is evaluated, because the numbers
        // it decides are drawn long before the render block is bound. An expression there
        // would load one arrangement and describe another.
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) =
            TestSource.Load("render { seed: 6 + 1 }\nsphere { }");

        Assert.Null(scene);
        Assert.Contains(diagnostics, d => d.Message.Contains("must be a plain number"));
    }

    [Fact]
    public void Accepts_a_negative_seed()
    {
        Scene scene = TestSource.LoadValid("render { seed: -12 }\nsphere { }");

        Assert.Equal(-12, scene.Render.Seed);
    }

    [Fact]
    public void Refuses_a_fractional_seed()
    {
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) =
            TestSource.Load("render { seed: 1.5 }\nsphere { }");

        Assert.Null(scene);
        Assert.Contains(diagnostics, d => d.Message.Contains("whole number"));
    }

    [Fact]
    public void Sees_the_built_ins_inside_an_included_fragment()
    {
        // A fragment runs in a frame of its own over the same built-ins, so 'random' means
        // there exactly what it means in the scene file -- including which seed it draws from.
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) = TestSource.LoadFiles(
            "scene.chroma",
            ("scene.chroma",
                TestSource.Camera
                + "render { seed: 4 }\n"
                + "include \"posts.chroma\";\n"
                + "sphere { center: [post(1), 0, 0] }\n"
                + "sphere { center: [random(1), 0, 0] }\n"),
            ("posts.chroma", "function post(i) { return random(i); }\n"));

        Assert.NotNull(scene);
        Assert.Empty(diagnostics);

        Assert.Equal(
            Assert.IsType<Sphere>(scene.Roots[0]).Center.X,
            Assert.IsType<Sphere>(scene.Roots[1]).Center.X);
    }

    [Fact]
    public void Refuses_a_seed_written_in_an_included_fragment()
    {
        // The early pass reads the scene file's text and no other, so a seed hiding in a
        // fragment would silently build the scene with the default. Reported instead.
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) = TestSource.LoadFiles(
            "scene.chroma",
            ("scene.chroma", TestSource.Camera + "include \"settings.chroma\";\nsphere { }\n"),
            ("settings.chroma", "render { seed: 21 }\n"));

        Assert.Null(scene);
        Assert.Contains(diagnostics, d => d.Message.Contains("in the scene file itself"));
    }
}
