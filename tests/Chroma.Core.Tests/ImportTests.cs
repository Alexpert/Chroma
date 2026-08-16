using System.Numerics;
using Chroma.Core.Model;
using Chroma.Core.Model.Geometry.Primitives;
using Chroma.Core.Sdl.Source;

namespace Chroma.Core.Tests;

/// <summary>
/// <c>import</c>, and the visibility rule that makes a file safe to reuse.
/// </summary>
/// <remarks>
/// The rule is asymmetric and each direction is tested here, because either one on its own
/// looks arbitrary. An imported file's bindings travel <b>out</b>, so a file of materials is worth
/// importing; the importer's bindings do not travel <b>in</b>, so the file means the
/// same thing wherever it is dropped.
/// </remarks>
public sealed class ImportTests
{
    private const string Camera = "camera { position: [0, 0, 5], lookAt: [0, 0, 0] }\n";

    [Fact]
    public void An_imported_file_contributes_its_solids()
    {
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) = TestSource.LoadFiles(
            "main.chroma",
            ("main.chroma", Camera + "import \"parts.chroma\";\nbox { }"),
            ("parts.chroma", "sphere { radius: 2 }"));

        Assert.NotNull(scene);
        Assert.Empty(diagnostics);
        Assert.Equal(2f, Assert.IsType<Sphere>(scene.Roots[0]).Radius);
    }

    [Fact]
    public void An_imported_files_bindings_are_visible_to_the_importer()
    {
        (Scene? scene, _) = TestSource.LoadFiles(
            "main.chroma",
            ("main.chroma", Camera + "import \"palette.chroma\";\nsphere { material: gold }"),
            ("palette.chroma", "let gold = material { color: [1, 0.7, 0.3] };"));

        Assert.NotNull(scene);
        Assert.Equal(new Vector3(1f, 0.7f, 0.3f), scene.Roots[0].Material!.Color);
    }

    [Fact]
    public void An_imported_file_cannot_see_the_importers_bindings()
    {
        // This is what a textual include would get wrong. The fragment names 'radius', the
        // includer defines it, and the fragment still fails — so it cannot come to depend on
        // a host scene by accident, and cannot be broken by one.
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) = TestSource.LoadFiles(
            "main.chroma",
            ("main.chroma", Camera + "let radius = 2;\nimport \"part.chroma\";"),
            ("part.chroma", "sphere { radius: radius }"));

        Assert.Null(scene);
        Assert.Contains(diagnostics, d => d.Message.Contains("unknown name 'radius'"));
    }

    [Fact]
    public void A_name_defined_on_both_sides_is_reported_against_the_import()
    {
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) = TestSource.LoadFiles(
            "main.chroma",
            ("main.chroma", Camera + "let red = material { };\nimport \"palette.chroma\";"),
            ("palette.chroma", "let red = material { };"));

        Assert.Null(scene);
        Assert.Contains(
            diagnostics,
            d => d.Message.Contains("defines 'red', which is already defined here"));
    }

    [Fact]
    public void A_diagnostic_inside_an_imported_file_names_that_file_and_its_line()
    {
        // The property seven iterations protected, and the one a preprocessor would forfeit:
        // the position belongs to the file the mistake is in.
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) = TestSource.LoadFiles(
            "main.chroma",
            ("main.chroma", Camera + "import \"part.chroma\";"),
            ("part.chroma", "// a comment first\nsphere { raduis: 1 }"));

        Assert.Null(scene);

        Diagnostic error = Assert.Single(diagnostics);
        Assert.Contains("unknown field 'raduis'", error.Message);
        Assert.EndsWith("part.chroma", error.Source.Path);
        Assert.Equal((2, 10), error.Position);
    }

    [Fact]
    public void An_imported_file_may_import_one_of_its_own()
    {
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) = TestSource.LoadFiles(
            "main.chroma",
            ("main.chroma", Camera + "import \"outer.chroma\";\nsphere { material: gold }"),
            ("outer.chroma", "import \"inner.chroma\";"),
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
            ("main.chroma", Camera + "import \"a.chroma\";"),
            ("a.chroma", "import \"b.chroma\";"),
            ("b.chroma", "import \"a.chroma\";"));

        Assert.Null(scene);
        Assert.Contains(diagnostics, d => d.Message.Contains("may not form a cycle"));
    }

    [Fact]
    public void A_file_that_imports_itself_is_refused_on_the_first_attempt()
    {
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) = TestSource.LoadFiles(
            "main.chroma",
            ("main.chroma", Camera + "import \"main.chroma\";"));

        Assert.Null(scene);
        Assert.Single(diagnostics, d => d.Message.Contains("may not form a cycle"));
    }

    [Fact]
    public void A_missing_file_is_reported_at_the_import()
    {
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) = TestSource.LoadFiles(
            "main.chroma",
            ("main.chroma", Camera + "import \"nowhere.chroma\";"));

        Assert.Null(scene);

        Diagnostic error = Assert.Single(diagnostics);
        Assert.Contains("cannot read", error.Message);
        Assert.Equal("\"nowhere.chroma\"", TestSource.TextAt(error));
    }

    [Fact]
    public void An_import_resolves_against_the_file_that_wrote_it()
    {
        // Not against the working directory: a folder of files that import each other
        // has to keep working wherever the renderer is run from.
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) = TestSource.LoadFiles(
            "main.chroma",
            ("main.chroma", Camera + "import \"outer.chroma\";"),
            ("outer.chroma", "import \"inner.chroma\";"),
            ("inner.chroma", "sphere { radius: 3 }"));

        Assert.NotNull(scene);
        Assert.Empty(diagnostics);
        Assert.Equal(3f, Assert.IsType<Sphere>(scene.Roots[0]).Radius);
    }

    [Fact]
    public void The_old_keyword_is_reported_and_names_the_new_one()
    {
        // The word was wrong the whole time: this has never been textual insertion. Renaming
        // it changed nothing about what it does, so the file is read on as an import and every
        // later mistake in it is still found on the same run.
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) = TestSource.LoadFiles(
            "main.chroma",
            ("main.chroma", Camera + "include \"part.chroma\";"),
            ("part.chroma", "sphere { radius: 3 }"));

        Assert.Null(scene);
        Assert.Contains(
            diagnostics,
            d => d.Message.Contains("'include' is the keyword this language used to have"));
    }

    [Fact]
    public void An_alias_puts_the_exports_behind_a_name()
    {
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) = TestSource.LoadFiles(
            "main.chroma",
            ("main.chroma",
                Camera
                + "import \"palette.chroma\" as palette;\n"
                + "sphere { radius: 2, material: palette.gold }"),
            ("palette.chroma", "let gold = material { color: [1, 0.8, 0.2] };"));

        Assert.NotNull(scene);
        Assert.Empty(diagnostics);
        Assert.Equal("gold", Assert.IsType<Sphere>(scene.Roots[0]).Material!.Name);
    }

    [Fact]
    public void An_alias_does_not_put_the_exports_anywhere_else()
    {
        // The point of the form: the name is reached where it is used, and nothing lands in
        // the importing scope to collide with anything.
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) = TestSource.LoadFiles(
            "main.chroma",
            ("main.chroma",
                Camera + "import \"palette.chroma\" as palette;\nsphere { material: gold }"),
            ("palette.chroma", "let gold = material { color: [1, 0.8, 0.2] };"));

        Assert.Null(scene);
        Assert.Contains(diagnostics, d => d.Message.Contains("unknown name 'gold'"));
    }

    [Fact]
    public void A_function_is_called_through_its_module()
    {
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) = TestSource.LoadFiles(
            "main.chroma",
            ("main.chroma",
                Camera + "import \"shapes.chroma\" as shapes;\nshapes.bead(4)"),
            ("shapes.chroma", "function bead(r) { return sphere { radius: r }; }"));

        Assert.NotNull(scene);
        Assert.Empty(diagnostics);
        Assert.Equal(4f, Assert.IsType<Sphere>(scene.Roots[0]).Radius);
    }

    [Fact]
    public void A_struct_type_is_built_through_its_module()
    {
        // A type name reached through a module still opens a block, which needs the parser to
        // read '{' after a '.' the way it reads '('. Without it the block parses as a separate
        // anonymous literal and the errors land nowhere near the mistake.
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) = TestSource.LoadFiles(
            "main.chroma",
            ("main.chroma",
                Camera
                + "import \"shapes.chroma\" as shapes;\n"
                + "sphere { radius: shapes.Post { at: 3, height: 7 }.height }"),
            ("shapes.chroma", "struct Post { at, height }"));

        Assert.NotNull(scene);
        Assert.Empty(diagnostics);
        Assert.Equal(7f, Assert.IsType<Sphere>(scene.Roots[0]).Radius);
    }

    [Fact]
    public void Reports_a_qualified_block_whose_name_is_not_a_struct_type()
    {
        // A node type is the other thing a block can be, and nothing exports one: a node name
        // is looked up by a binder rather than bound.
        (_, IReadOnlyList<Diagnostic> diagnostics) = TestSource.LoadFiles(
            "main.chroma",
            ("main.chroma",
                Camera + "import \"part.chroma\" as part;\npart.tone { radius: 1 }"),
            ("part.chroma", "let tone = 1;"));

        Assert.Contains(
            diagnostics,
            d => d.Message.Contains("rather than a struct type, so it cannot open a block"));
    }

    [Fact]
    public void Two_modules_may_both_define_the_same_name()
    {
        // The collision the flat form reports and cannot solve, which is the whole reason the
        // aliased form exists.
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) = TestSource.LoadFiles(
            "main.chroma",
            ("main.chroma",
                Camera
                + "import \"warm.chroma\" as warm;\n"
                + "import \"cool.chroma\" as cool;\n"
                + "sphere { radius: warm.tone }\n"
                + "sphere { radius: cool.tone }"),
            ("warm.chroma", "let tone = 1;"),
            ("cool.chroma", "let tone = 2;"));

        Assert.NotNull(scene);
        Assert.Empty(diagnostics);
        Assert.Equal(1f, Assert.IsType<Sphere>(scene.Roots[0]).Radius);
        Assert.Equal(2f, Assert.IsType<Sphere>(scene.Roots[1]).Radius);
    }

    [Fact]
    public void The_flat_form_names_the_aliased_one_when_it_collides()
    {
        (_, IReadOnlyList<Diagnostic> diagnostics) = TestSource.LoadFiles(
            "main.chroma",
            ("main.chroma", Camera + "let tone = 1;\nimport \"warm.chroma\";\nsphere { }"),
            ("warm.chroma", "let tone = 2;"));

        Assert.Contains(diagnostics, d => d.Message.Contains("as …' to reach it by name instead"));
    }

    [Theory]
    [InlineData("let")]
    [InlineData("function")]
    [InlineData("struct")]
    public void Private_keeps_a_declaration_inside_the_file_that_made_it(string kind)
    {
        // The marker is on what stays rather than on what leaves, which is the way round that
        // leaves the common case unannotated.
        string declaration = kind switch
        {
            "let" => "private let helper = 1;",
            "function" => "private function helper() { return 1; }",
            _ => "private struct helper { x }",
        };

        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) = TestSource.LoadFiles(
            "main.chroma",
            ("main.chroma", Camera + "import \"part.chroma\";\nsphere { radius: helper }"),
            ("part.chroma", declaration + "\nlet published = 2;"));

        Assert.Null(scene);
        Assert.Contains(diagnostics, d => d.Message.Contains("unknown name 'helper'"));
    }

    [Fact]
    public void Private_does_not_hide_a_name_from_the_file_that_declared_it()
    {
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) = TestSource.LoadFiles(
            "main.chroma",
            ("main.chroma", Camera + "import \"part.chroma\";\nsphere { radius: published }"),
            ("part.chroma", "private let helper = 5;\nlet published = helper;"));

        Assert.NotNull(scene);
        Assert.Empty(diagnostics);
        Assert.Equal(5f, Assert.IsType<Sphere>(scene.Roots[0]).Radius);
    }

    [Fact]
    public void Private_hides_a_name_from_an_alias_too()
    {
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) = TestSource.LoadFiles(
            "main.chroma",
            ("main.chroma",
                Camera + "import \"part.chroma\" as part;\nsphere { radius: part.helper }"),
            ("part.chroma", "private let helper = 5;\nlet published = 2;"));

        Assert.Null(scene);
        Assert.Contains(
            diagnostics,
            d => d.Message.Contains("does not export 'helper'; it exports 'published'"));
    }

    [Fact]
    public void Reports_private_in_front_of_something_that_is_not_a_declaration()
    {
        (_, IReadOnlyList<Diagnostic> diagnostics) =
            TestSource.Load("private sphere { }");

        Assert.Contains(
            diagnostics,
            d => d.Message.Contains("'private' belongs in front of a 'let', a 'function' or a 'struct'"));
    }

    [Fact]
    public void Reports_a_call_through_something_that_is_not_a_module()
    {
        (_, IReadOnlyList<Diagnostic> diagnostics) =
            TestSource.Load("let n = 3;\nsphere { radius: n.f(1) }");

        Assert.Contains(diagnostics, d => d.Message.Contains("is not a module"));
    }
}
