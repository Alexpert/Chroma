using ChromaTest.Core.Model;
using ChromaTest.Core.Sdl.Source;

namespace ChromaTest.Core.Tests;

public sealed class DiagnosticTests
{
    [Fact]
    public void Points_at_the_exact_line_and_column()
    {
        (_, IReadOnlyList<Diagnostic> diagnostics) = TestSource.LoadRaw(
            """
            camera { position: [0, 0, 5] }
            sphere {
              raduis: 1
            }
            """);

        Diagnostic error = Assert.Single(diagnostics);
        Assert.Equal((3, 3), error.Position);
        Assert.Equal("unknown field 'raduis' on 'sphere'", error.Message);
    }

    [Fact]
    public void Formats_as_path_line_column_severity_message()
    {
        (_, IReadOnlyList<Diagnostic> diagnostics) = TestSource.Load("sphere { raduis: 1 }");

        Assert.Equal(
            "test.chroma:2:10: error: unknown field 'raduis' on 'sphere'",
            Assert.Single(diagnostics).ToString());
    }

    [Fact]
    public void Reports_every_problem_in_one_pass()
    {
        // The whole reason diagnostics are collected rather than thrown: fixing a scene
        // file one error per run is a game of twenty questions.
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) = TestSource.Load(
            """
            let r = 1;
            let r = 2;
            sphere { raduis: r }
            box { min: [-1, -1] }
            difference { box { } }
            """);

        Assert.Null(scene);
        Assert.Equal(4, diagnostics.Count);
    }

    [Fact]
    public void Returns_diagnostics_in_source_order()
    {
        // The lexer, parser and binder each sweep the file separately, so raw insertion
        // order is by phase -- which is not the order anyone reads a file in.
        (_, IReadOnlyList<Diagnostic> diagnostics) = TestSource.Load(
            """
            sphere { raduis: 1 }
            box { min: [1, 2] }
            """);

        Assert.Equal(2, diagnostics.Count);
        Assert.True(
            diagnostics[0].Position.Line < diagnostics[1].Position.Line,
            "diagnostics should be ordered by position");
    }

    [Fact]
    public void Reports_a_missing_camera()
    {
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) = TestSource.LoadRaw("sphere { }");

        Assert.Null(scene);
        Assert.Contains(diagnostics, d => d.Message.Contains("declares no camera"));
    }

    [Fact]
    public void Reports_a_second_camera()
    {
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) =
            TestSource.Load("camera { position: [0, 0, 9] }");

        Assert.Null(scene);
        Assert.Contains(diagnostics, d => d.Message.Contains("only one camera"));
    }

    [Fact]
    public void Does_not_complain_about_a_missing_camera_when_parsing_already_failed()
    {
        // A file that could not be parsed has no camera for reasons already explained;
        // adding a second complaint would send the reader chasing the wrong problem.
        (_, IReadOnlyList<Diagnostic> diagnostics) = TestSource.LoadRaw("sphere { radius: }");

        Assert.DoesNotContain(diagnostics, d => d.Message.Contains("declares no camera"));
    }

    [Theory]
    [InlineData("difference { box { } }", "'difference' needs at least 2 operands, found 1")]
    [InlineData("intersection { box { } }", "'intersection' needs at least 2 operands, found 1")]
    [InlineData("union { }", "'union' needs at least 1 operand, found 0")]
    public void Reports_operand_arity(string body, string expected)
    {
        (_, IReadOnlyList<Diagnostic> diagnostics) = TestSource.Load(body);

        Assert.Contains(diagnostics, d => d.Message == expected);
    }

    [Fact]
    public void Reports_a_field_of_the_wrong_type()
    {
        (_, IReadOnlyList<Diagnostic> diagnostics) = TestSource.Load("sphere { radius: [1, 2, 3] }");

        Assert.Contains(
            diagnostics,
            d => d.Message == "field 'radius' expects a number, found a vector of 3 components");
    }

    [Fact]
    public void Reports_a_vector_of_the_wrong_length()
    {
        (_, IReadOnlyList<Diagnostic> diagnostics) = TestSource.Load("sphere { center: [1, 2] }");

        Assert.Contains(
            diagnostics,
            d => d.Message == "field 'center' expects a vector of 3 components, found a vector of 2 components");
    }

    [Fact]
    public void Reports_a_missing_required_field()
    {
        (_, IReadOnlyList<Diagnostic> diagnostics) = TestSource.Load("pointLight { color: [1, 1, 1] }");

        Assert.Contains(diagnostics, d => d.Message == "'pointLight' requires a 'position' field");
    }

    [Fact]
    public void Reports_a_repeated_field()
    {
        (_, IReadOnlyList<Diagnostic> diagnostics) = TestSource.Load("sphere { radius: 1, radius: 2 }");

        Assert.Contains(
            diagnostics,
            d => d.Message == "field 'radius' is set more than once on 'sphere'");
    }

    [Fact]
    public void Reports_an_unknown_node_type()
    {
        (_, IReadOnlyList<Diagnostic> diagnostics) = TestSource.Load("torus { }");

        Assert.Contains(diagnostics, d => d.Message == "unknown node type 'torus'");
    }

    [Fact]
    public void Reports_a_child_on_a_node_that_takes_none()
    {
        (_, IReadOnlyList<Diagnostic> diagnostics) = TestSource.Load("sphere { box { } }");

        Assert.Contains(diagnostics, d => d.Message == "'sphere' does not take child objects");
    }

    [Fact]
    public void Reports_a_material_used_where_a_solid_belongs()
    {
        (_, IReadOnlyList<Diagnostic> diagnostics) = TestSource.Load("union { material { } }");

        Assert.Contains(diagnostics, d => d.Message.Contains("expected a solid"));
    }

    [Fact]
    public void Reports_a_degenerate_camera()
    {
        (_, IReadOnlyList<Diagnostic> diagnostics) = TestSource.LoadRaw(
            "camera { position: [0, 0, 5], lookAt: [0, 0, 0], up: [0, 0, 1] }");

        Assert.Contains(diagnostics, d => d.Message.Contains("parallel to the direction of view"));
    }

    [Fact]
    public void Reports_box_corners_the_wrong_way_round()
    {
        (_, IReadOnlyList<Diagnostic> diagnostics) = TestSource.Load("box { min: [1, 1, 1], max: [-1, -1, -1] }");

        Assert.Contains(diagnostics, d => d.Message.Contains("less than or equal to 'max'"));
    }

    [Fact]
    public void Reports_a_zero_length_cylinder()
    {
        (_, IReadOnlyList<Diagnostic> diagnostics) =
            TestSource.Load("cylinder { base: [0, 0, 0], cap: [0, 0, 0] }");

        Assert.Contains(diagnostics, d => d.Message.Contains("'base' and 'cap' to be different"));
    }

    [Fact]
    public void Reports_a_top_level_value_that_is_not_a_scene_item()
    {
        (_, IReadOnlyList<Diagnostic> diagnostics) = TestSource.Load("material { }");

        Assert.Contains(diagnostics, d => d.Message.Contains("cannot appear on its own at the top level"));
    }
}
