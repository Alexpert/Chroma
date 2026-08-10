using Chroma.Core.Model;
using Chroma.Core.Sdl.Source;

namespace Chroma.Core.Tests;

/// <summary>
/// The <c>render</c> node: settings that belong to a scene rather than to the build.
/// </summary>
public sealed class RenderSettingsTests
{
    [Fact]
    public void Defaults_when_the_scene_declares_no_render_block()
    {
        // Unlike the camera, absence is not an error: every setting has a usable default,
        // and most scenes have no reason to say anything about them.
        Scene scene = TestSource.LoadValid("sphere { }");

        Assert.Equal(RenderSettings.Default.MaxBounces, scene.Render.MaxBounces);
        Assert.Equal(RenderSettings.Default.Exposure, scene.Render.Exposure);
    }

    [Fact]
    public void Reads_both_settings()
    {
        Scene scene = TestSource.LoadValid("render { maxBounces: 7, exposure: 1.5 }\nsphere { }");

        Assert.Equal(7, scene.Render.MaxBounces);
        Assert.Equal(1.5f, scene.Render.Exposure);
    }

    [Fact]
    public void Refuses_a_second_render_block()
    {
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) =
            TestSource.Load("render { }\nrender { }\nsphere { }");

        Assert.Null(scene);
        Assert.Contains(diagnostics, d => d.Message.Contains("only one render block"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(64)]
    public void Reports_a_bounce_count_outside_the_supported_range(int bounces)
    {
        // Reported rather than clamped: the loop runs per pixel per frame, so an absurd
        // depth is a typing mistake, and a frozen driver costs far more to diagnose.
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) =
            TestSource.Load($"render {{ maxBounces: {bounces} }}\nsphere {{ }}");

        Assert.Null(scene);
        Assert.Contains(
            diagnostics,
            d => d.Message.Contains(
                $"between {RenderSettings.MinBounces} and {RenderSettings.MaxAllowedBounces}"));
    }

    [Fact]
    public void Reports_a_fractional_bounce_count()
    {
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) =
            TestSource.Load("render { maxBounces: 2.5 }\nsphere { }");

        Assert.Null(scene);
        Assert.Contains(diagnostics, d => d.Message.Contains("whole number"));
    }

    [Fact]
    public void Reports_a_fractional_bounce_count_under_a_comma_decimal_culture()
    {
        // The diagnostic prints the offending value, and printing it with the machine's
        // culture would put a comma in a message about a file that uses a point.
        TestSource.InCommaDecimalCulture(() =>
        {
            (_, IReadOnlyList<Diagnostic> diagnostics) =
                TestSource.Load("render { maxBounces: 2.5 }\nsphere { }");

            Assert.Contains(diagnostics, d => d.Message.Contains("found 2.5"));
        });
    }
}
