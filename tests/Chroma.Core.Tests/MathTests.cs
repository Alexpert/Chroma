using System.Numerics;
using Chroma.Core.Model;
using Chroma.Core.Model.Geometry;
using Chroma.Core.Model.Geometry.Primitives;
using Chroma.Core.Sdl.Source;

namespace Chroma.Core.Tests;

/// <summary>
/// <c>PI</c>, the function library, and the radian mode: the three halves of one gap.
/// </summary>
public sealed class MathTests
{
    private static double Value(string expression) =>
        Assert.IsType<Sphere>(
            TestSource.LoadValid($"sphere {{ center: [{expression}, 0, 0] }}").Roots[0]).Center.X;

    private static IReadOnlyList<double> Components(string expression)
    {
        Vector3 centre = Assert.IsType<Sphere>(
            TestSource.LoadValid($"sphere {{ center: {expression} }}").Roots[0]).Center;

        return [centre.X, centre.Y, centre.Z];
    }

    [Theory]
    [InlineData("sin(0)", 0)]
    [InlineData("cos(0)", 1)]
    [InlineData("tan(0)", 0)]
    [InlineData("asin(0)", 0)]
    [InlineData("acos(1)", 0)]
    [InlineData("atan(0)", 0)]
    [InlineData("atan2(0, 1)", 0)]
    [InlineData("sqrt(9)", 3)]
    [InlineData("exp(0)", 1)]
    [InlineData("log(1)", 0)]
    [InlineData("pow(2, 10)", 1024)]
    [InlineData("abs(-2.5)", 2.5)]
    [InlineData("sign(-7)", -1)]
    [InlineData("sign(0)", 0)]
    [InlineData("floor(1.8)", 1)]
    [InlineData("ceil(1.2)", 2)]
    [InlineData("min(3, 5)", 3)]
    [InlineData("max(3, 5)", 5)]
    [InlineData("clamp(9, 0, 4)", 4)]
    [InlineData("clamp(-9, 0, 4)", 0)]
    public void Evaluates_the_scalar_library(string expression, double expected)
    {
        Assert.Equal(expected, Value(expression), 5);
    }

    [Theory]

    // Away from zero at a half, which is C's 'round' and what a reader expects of the word.
    // .NET's default is banker's rounding, under which round(0.5) is 0 and round(2.5) is 2.
    [InlineData("round(0.5)", 1)]
    [InlineData("round(1.5)", 2)]
    [InlineData("round(2.5)", 3)]
    [InlineData("round(-0.5)", -1)]
    public void Rounds_a_half_away_from_zero(string expression, double expected)
    {
        Assert.Equal(expected, Value(expression), 5);
    }

    [Fact]
    public void Supplies_PI_as_a_constant()
    {
        // To six places, because the value is read back out of the scene model, where a
        // coordinate is a 32-bit float. The binding itself is the full double.
        Assert.Equal(Math.PI, Value("PI"), 6);

        // And it is what makes radians usable: the trigonometric functions take them.
        Assert.Equal(1.0, Value("sin(PI / 2)"), 6);
    }

    [Fact]
    public void PI_is_a_built_in_and_nothing_assigns_to_it()
    {
        // Asked of the frame rather than of the value's type, so a constant is as unwritable
        // as a function is.
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) =
            TestSource.Load("PI = 3;\nsphere { }");

        Assert.Null(scene);
        Assert.Contains(diagnostics, d => d.Message.Contains("nothing assigns to one"));
    }

    [Fact]
    public void PI_cannot_be_redeclared_either()
    {
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) =
            TestSource.Load("let PI = 3;\nsphere { }");

        Assert.Null(scene);
        Assert.Contains(diagnostics, d => d.Message.Contains("'PI' is a built-in of the language"));
    }

    [Theory]
    [InlineData("length([3, 4])", 5)]
    [InlineData("length([1, 2, 2])", 3)]
    [InlineData("dot([1, 2, 3], [4, 5, 6])", 32)]
    public void Evaluates_the_vector_library(string expression, double expected)
    {
        Assert.Equal(expected, Value(expression), 5);
    }

    [Fact]
    public void Normalizes_a_vector()
    {
        Assert.Equal([0.6, 0.8, 0.0], Components("normalize([3, 4, 0])").Select(c => Math.Round(c, 5)));
    }

    [Fact]
    public void Takes_a_cross_product()
    {
        Assert.Equal([0.0, 0.0, 1.0], Components("cross([1, 0, 0], [0, 1, 0])"));
    }

    [Theory]

    // These four were recorded as missing from the language rather than as functions nobody
    // had written: there was no way to hand a vector to a function or get one back.
    [InlineData("length(3)", "'v' of 'length' is a vector, found a number")]
    [InlineData("normalize([[1, 2]])", "'v' of 'normalize' is a vector, found an array of 1 element")]
    [InlineData("normalize([0, 0])", "'normalize' has no answer for a vector of length zero")]
    [InlineData("dot([1, 2], [1, 2, 3])", "'dot' needs two vectors of the same length, found 2 and 3")]
    [InlineData("cross([1, 2], [3, 4])", "'cross' needs two vectors of 3 components")]
    [InlineData("clamp(1, 4, 0)", "'clamp' needs 'lo' to be no greater than 'hi'")]
    public void Reports_what_a_function_cannot_answer(string expression, string message)
    {
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) =
            TestSource.Load($"sphere {{ radius: {expression} }}");

        Assert.Null(scene);
        Assert.Contains(diagnostics, d => d.Message.Contains(message));
    }

    [Fact]
    public void A_domain_error_answers_the_way_division_by_zero_does()
    {
        // Not reported, deliberately: '1 / 0' has produced infinity since the language had
        // arithmetic, and checking the domain of 'sqrt' while leaving '/' alone would be an
        // inconsistency rather than a safety net.
        Assert.True(double.IsNaN(Value("sqrt(-1)")));
    }

    [Fact]
    public void Angles_are_degrees_unless_the_scene_says_otherwise()
    {
        Scene scene = TestSource.LoadValid("sphere { rotate: [0, 90, 0] }");

        Assert.False(scene.Render.AnglesInRadians);
        Assert.Equal(90f, Rotation(scene).Y);
    }

    [Fact]
    public void Reads_both_angular_fields_in_radians_when_the_scene_says_so()
    {
        Scene scene = LoadRawValid(
            "render { angles: \"radians\" }\n"
            + "camera { position: [0, 0, 5], lookAt: [0, 0, 0], fov: PI / 4 }\n"
            + "sphere { rotate: [0, PI / 2, 0] }");

        // The model still holds degrees, so nothing downstream of the binders knows the mode
        // existed and the hierarchy dump of a scene that says nothing is unchanged.
        Assert.Equal(45f, scene.Camera.FovDegrees, 4);
        Assert.Equal(90f, Rotation(scene).Y, 4);
    }

    [Fact]
    public void The_mode_applies_to_a_camera_written_above_the_render_block()
    {
        // The render block binds before everything else for exactly this reason: a scene that
        // names the mode at the bottom of the file means it for the camera at the top.
        Scene scene = TestSource.LoadValid(
            "sphere { rotate: [0, PI, 0] }\n"
            + "render { angles: \"radians\" }");

        Assert.Equal(180f, Rotation(scene).Y, 4);
    }

    [Fact]
    public void The_default_field_of_view_follows_the_mode_too()
    {
        // 45 degrees either way. A scene in radians that omits 'fov' must not get 45 radians.
        Scene scene = LoadRawValid(
            "render { angles: \"radians\" }\ncamera { position: [0, 0, 5], lookAt: [0, 0, 0] }");

        Assert.Equal(45f, scene.Camera.FovDegrees, 4);
    }

    /// <summary>Loads a scene that declares its own camera, and fails on any diagnostic.</summary>
    private static Scene LoadRawValid(string text)
    {
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) = TestSource.LoadRaw(text);

        Assert.True(
            scene is not null,
            "expected a valid scene, got: " + string.Join("; ", diagnostics.Select(d => d.Message)));

        return scene!;
    }

    [Fact]
    public void Reports_an_angle_mode_that_is_not_one_of_the_two_words()
    {
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) =
            TestSource.Load("render { angles: \"turns\" }\nsphere { }");

        Assert.Null(scene);
        Assert.Contains(
            diagnostics,
            d => d.Message.Contains("expects one of \"degrees\", \"radians\""));
    }

    private static Vector3 Rotation(Scene scene)
    {
        Transform transform = scene.Roots[0].Transform;
        return transform.Steps.First(s => s.Kind == TransformKind.Rotate).Value;
    }
}
