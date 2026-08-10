using System.Numerics;
using Chroma.Core.Model;
using Chroma.Core.Model.Geometry.Primitives;
using Chroma.Core.Sdl.Source;

namespace Chroma.Core.Tests;

/// <summary>
/// <c>include</c>, and the visibility rule that makes a fragment safe to reuse.
/// </summary>
/// <remarks>
/// The rule is asymmetric and each direction is tested here, because either one on its own
/// looks arbitrary. A fragment's bindings travel <b>out</b>, so a file of materials is worth
/// including; the includer's bindings do not travel <b>in</b>, so the fragment means the
/// same thing wherever it is dropped.
/// </remarks>
public sealed class IncludeTests
{
    private const string Camera = "camera { position: [0, 0, 5], lookAt: [0, 0, 0] }\n";

    [Fact]
    public void An_included_fragment_contributes_its_solids()
    {
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) = TestSource.LoadFiles(
            "main.chroma",
            ("main.chroma", Camera + "include \"parts.chroma\";\nbox { }"),
            ("parts.chroma", "sphere { radius: 2 }"));

        Assert.NotNull(scene);
        Assert.Empty(diagnostics);
        Assert.Equal(2f, Assert.IsType<Sphere>(scene.Roots[0]).Radius);
    }

    [Fact]
    public void An_included_fragments_bindings_are_visible_to_the_includer()
    {
        (Scene? scene, _) = TestSource.LoadFiles(
            "main.chroma",
            ("main.chroma", Camera + "include \"palette.chroma\";\nsphere { material: gold }"),
            ("palette.chroma", "let gold = material { color: [1, 0.7, 0.3] };"));

        Assert.NotNull(scene);
        Assert.Equal(new Vector3(1f, 0.7f, 0.3f), scene.Roots[0].Material!.Color);
    }

    [Fact]
    public void An_included_fragment_cannot_see_the_includers_bindings()
    {
        // This is what a textual include would get wrong. The fragment names 'radius', the
        // includer defines it, and the fragment still fails — so it cannot come to depend on
        // a host scene by accident, and cannot be broken by one.
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) = TestSource.LoadFiles(
            "main.chroma",
            ("main.chroma", Camera + "let radius = 2;\ninclude \"part.chroma\";"),
            ("part.chroma", "sphere { radius: radius }"));

        Assert.Null(scene);
        Assert.Contains(diagnostics, d => d.Message.Contains("unknown name 'radius'"));
    }

    [Fact]
    public void A_name_defined_on_both_sides_is_reported_against_the_include()
    {
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) = TestSource.LoadFiles(
            "main.chroma",
            ("main.chroma", Camera + "let red = material { };\ninclude \"palette.chroma\";"),
            ("palette.chroma", "let red = material { };"));

        Assert.Null(scene);
        Assert.Contains(
            diagnostics,
            d => d.Message.Contains("defines 'red', which is already defined here"));
    }

    [Fact]
    public void A_diagnostic_inside_a_fragment_names_the_fragment_and_its_line()
    {
        // The property seven iterations protected, and the one a preprocessor would forfeit:
        // the position belongs to the file the mistake is in.
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) = TestSource.LoadFiles(
            "main.chroma",
            ("main.chroma", Camera + "include \"part.chroma\";"),
            ("part.chroma", "// a comment first\nsphere { raduis: 1 }"));

        Assert.Null(scene);

        Diagnostic error = Assert.Single(diagnostics);
        Assert.Contains("unknown field 'raduis'", error.Message);
        Assert.EndsWith("part.chroma", error.Source.Path);
        Assert.Equal((2, 10), error.Position);
    }

    [Fact]
    public void A_fragment_may_include_a_fragment_of_its_own()
    {
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) = TestSource.LoadFiles(
            "main.chroma",
            ("main.chroma", Camera + "include \"outer.chroma\";\nsphere { material: gold }"),
            ("outer.chroma", "include \"inner.chroma\";"),
            ("inner.chroma", "let gold = material { color: [1, 0.7, 0.3] };"));

        Assert.NotNull(scene);
        Assert.Empty(diagnostics);
        Assert.Equal(new Vector3(1f, 0.7f, 0.3f), scene.Roots[0].Material!.Color);
    }

    [Fact]
    public void A_cycle_is_refused_rather_than_followed()
    {
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) = TestSource.LoadFiles(
            "main.chroma",
            ("main.chroma", Camera + "include \"a.chroma\";"),
            ("a.chroma", "include \"b.chroma\";"),
            ("b.chroma", "include \"a.chroma\";"));

        Assert.Null(scene);
        Assert.Contains(diagnostics, d => d.Message.Contains("may not form a cycle"));
    }

    [Fact]
    public void A_file_that_includes_itself_is_refused_on_the_first_attempt()
    {
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) = TestSource.LoadFiles(
            "main.chroma",
            ("main.chroma", Camera + "include \"main.chroma\";"));

        Assert.Null(scene);
        Assert.Single(diagnostics, d => d.Message.Contains("may not form a cycle"));
    }

    [Fact]
    public void A_missing_file_is_reported_at_the_include()
    {
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) = TestSource.LoadFiles(
            "main.chroma",
            ("main.chroma", Camera + "include \"nowhere.chroma\";"));

        Assert.Null(scene);

        Diagnostic error = Assert.Single(diagnostics);
        Assert.Contains("cannot read", error.Message);
        Assert.Equal("\"nowhere.chroma\"", TestSource.TextAt(error));
    }

    [Fact]
    public void An_include_resolves_against_the_file_that_wrote_it()
    {
        // Not against the working directory: a folder of fragments that include each other
        // has to keep working wherever the renderer is run from.
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) = TestSource.LoadFiles(
            "main.chroma",
            ("main.chroma", Camera + "include \"outer.chroma\";"),
            ("outer.chroma", "include \"inner.chroma\";"),
            ("inner.chroma", "sphere { radius: 3 }"));

        Assert.NotNull(scene);
        Assert.Empty(diagnostics);
        Assert.Equal(3f, Assert.IsType<Sphere>(scene.Roots[0]).Radius);
    }
}
