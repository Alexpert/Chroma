using System.Numerics;
using Chroma.Core.Compilation;
using Chroma.Core.Model;
using Chroma.Core.Model.Geometry;
using Chroma.Core.Model.Geometry.Operations;
using Chroma.Core.Model.Geometry.Primitives;
using Chroma.Core.Sdl.Binding;
using Chroma.Core.Sdl.Source;

// System.Numerics has a Plane of its own, and this file needs both it and Vector3.
using Plane = Chroma.Core.Model.Geometry.Primitives.Plane;

namespace Chroma.Core.Tests;

/// <summary>
/// Conditions and loops, observed through the scene they build.
/// </summary>
/// <remarks>
/// These are written against the scene rather than against the syntax tree on purpose. The
/// tree a loop produces is not the tree the parser produced, so asserting on the parse says
/// nothing about what the file means — and what it means is how many solids come out, where
/// they are, and which material they wear.
/// </remarks>
public sealed class ControlFlowTests
{
    [Fact]
    public void A_loop_emits_one_solid_per_iteration()
    {
        Scene scene = TestSource.LoadValid(
            "for (let i = 0; i < 4; i++) { sphere { center: [i, 0, 0] } }");

        Assert.Equal(4, scene.Roots.Count);
        Assert.Equal(
            [0f, 1f, 2f, 3f],
            scene.Roots.Select(r => Assert.IsType<Sphere>(r).Center.X));
    }

    [Fact]
    public void A_loop_counts_from_wherever_it_is_told_to()
    {
        Scene scene = TestSource.LoadValid(
            "for (let i = 2; i < 5; i++) { sphere { radius: i } }");

        Assert.Equal([2f, 3f, 4f], scene.Roots.Select(r => Assert.IsType<Sphere>(r).Radius));
    }

    [Fact]
    public void A_loop_may_count_downwards_or_in_steps()
    {
        // The step clause is an ordinary statement, so anything that changes the counter is
        // available — which is the whole of what the C-style header buys over a range.
        Scene scene = TestSource.LoadValid(
            """
            for (let i = 6; i > 0; i = i - 2) { sphere { radius: i } }
            for (let j = 3; j > 0; j--) { box { min: [j, j, j], max: [4, 4, 4] } }
            """);

        Assert.Equal(
            [6f, 4f, 2f],
            scene.Roots.OfType<Sphere>().Select(s => s.Radius));

        Assert.Equal(
            [3f, 2f, 1f],
            scene.Roots.OfType<Box>().Select(b => b.Min.X));
    }

    [Theory]
    [InlineData("let i = 0; i < 0; i++")]
    [InlineData("let i = 5; i < 0; i++")]
    public void A_condition_false_at_the_start_emits_nothing(string header)
    {
        // Not an error: a count that comes out zero is the ordinary way to write "none of
        // these", and refusing it would put a guard around every generated count.
        Scene scene = TestSource.LoadValid($"sphere {{ }} for ({header}) {{ box {{ }} }}");

        Assert.IsType<Sphere>(Assert.Single(scene.Roots));
    }

    [Fact]
    public void Nested_loops_multiply()
    {
        Scene scene = TestSource.LoadValid(
            """
            for (let x = 0; x < 3; x++) {
                for (let y = 0; y < 2; y++) {
                    sphere { center: [x, y, 0] }
                }
            }
            """);

        Assert.Equal(6, scene.Roots.Count);
        Assert.Equal(
            new Vector3(2f, 1f, 0f),
            Assert.IsType<Sphere>(scene.Roots[^1]).Center);
    }

    [Fact]
    public void A_loop_generates_the_operands_of_an_operator()
    {
        Scene scene = TestSource.LoadValid(
            """
            union {
                for (let i = 0; i < 3; i++) {
                    sphere { center: [i * 3, 0, 0], radius: 1 }
                }
            }
            """);

        Union union = Assert.IsType<Union>(Assert.Single(scene.Roots));
        Assert.Equal(3, union.Operands.Count);
    }

    [Fact]
    public void An_if_decides_whether_an_entry_exists()
    {
        Scene scene = TestSource.LoadValid(
            "for (let i = 0; i < 4; i++) { if (i < 2) { sphere { radius: i } } }");

        Assert.Equal(2, scene.Roots.Count);
    }

    [Fact]
    public void An_if_chooses_between_two_bodies()
    {
        Scene scene = TestSource.LoadValid(
            "for (let i = 0; i < 3; i++) { if (i == 1) { box { } } else { sphere { } } }");

        Assert.Collection(
            scene.Roots,
            r => Assert.IsType<Sphere>(r),
            r => Assert.IsType<Box>(r),
            r => Assert.IsType<Sphere>(r));
    }

    [Fact]
    public void An_else_if_chains()
    {
        Scene scene = TestSource.LoadValid(
            """
            for (let i = 0; i < 3; i++) {
                if (i == 0) { box { } } else if (i == 1) { sphere { } } else { plane { } }
            }
            """);

        Assert.Collection(
            scene.Roots,
            r => Assert.IsType<Box>(r),
            r => Assert.IsType<Sphere>(r),
            r => Assert.IsType<Plane>(r));
    }

    [Fact]
    public void The_braces_of_a_body_are_not_optional()
    {
        // They were, around a single statement, until the JavaScript revision. The message
        // has to name what is missing rather than the token that is there, because someone
        // writing this is porting a file rather than making a typo.
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) =
            TestSource.Load("if (true) sphere { }");

        Assert.Null(scene);
        Assert.Contains(
            diagnostics,
            d => d.Message.Contains("expected '{' to open the body of an 'if'"));
    }

    [Fact]
    public void An_if_is_not_a_value()
    {
        // 'if (c) a else b' was how a value was chosen before the ternary. The message names
        // the replacement, since that is what someone reading it needs.
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) =
            TestSource.Load("sphere { radius: if (true) 1 else 2 }");

        Assert.Null(scene);
        Assert.Contains(
            diagnostics,
            d => d.Message.Contains("write 'condition ? a : b' to choose between two"));
    }

    [Fact]
    public void A_ternary_chooses_between_two_values()
    {
        Scene scene = TestSource.LoadValid(
            """
            let red = material { color: [1, 0, 0] };
            let blue = material { color: [0, 0, 1] };

            for (let i = 0; i < 2; i++) { sphere { material: i == 0 ? red : blue } }
            """);

        Assert.Equal(new Vector3(1f, 0f, 0f), scene.Roots[0].Material!.Color);
        Assert.Equal(new Vector3(0f, 0f, 1f), scene.Roots[1].Material!.Color);
    }

    [Fact]
    public void A_ternary_chains_to_the_right()
    {
        // 'a ? x : b ? y : z' has to read as 'a ? x : (b ? y : z)', which is the else-if of
        // expressions. Grouping the other way would make the second arm a condition.
        Scene scene = TestSource.LoadValid(
            """
            for (let i = 0; i < 3; i++) {
                sphere { radius: i == 0 ? 1 : i == 1 ? 2 : 3 }
            }
            """);

        Assert.Equal([1f, 2f, 3f], scene.Roots.Select(r => Assert.IsType<Sphere>(r).Radius));
    }

    [Fact]
    public void Only_the_arm_taken_is_evaluated()
    {
        // The other arm names something that does not exist. If it were evaluated the load
        // would fail, and a scene could not use a ternary to guard a value at all.
        Scene scene = TestSource.LoadValid("sphere { radius: true ? 2 : missingName }");

        Assert.Equal(2f, Assert.IsType<Sphere>(scene.Roots[0]).Radius);
    }

    [Theory]
    [InlineData("1 < 2", true)]
    [InlineData("2 < 2", false)]
    [InlineData("2 <= 2", true)]
    [InlineData("3 > 2", true)]
    [InlineData("2 >= 3", false)]
    [InlineData("1 + 1 == 2", true)]
    [InlineData("1 != 2", true)]
    [InlineData("[1, 2] == [1, 2]", true)]
    [InlineData("[1, 2] == [1, 3]", false)]
    [InlineData("\"a\" == \"a\"", true)]
    [InlineData("true == false", false)]
    [InlineData("!true", false)]
    [InlineData("true && false", false)]
    [InlineData("true || false", true)]
    [InlineData("1 < 2 && 2 < 3 || false", true)]
    [InlineData("7 % 3 == 1", true)]
    [InlineData("8 % 2 == 0", true)]
    [InlineData("-1 % 2 == -1", true)]
    public void Evaluates_comparisons_and_boolean_operators(string expression, bool expected)
    {
        Scene scene = TestSource.LoadValid($"if ({expression}) {{ sphere {{ }} }}");

        Assert.Equal(expected, scene.Roots.Count == 1);
    }

    [Fact]
    public void Modulo_binds_as_tightly_as_multiplication()
    {
        // '1 + 5 % 3' is 3 and not 0. Getting this wrong is invisible until a checkerboard
        // comes out striped.
        Scene scene = TestSource.LoadValid("sphere { radius: 1 + 5 % 3 }");

        Assert.Equal(3f, Assert.IsType<Sphere>(scene.Roots[0]).Radius);
    }

    [Fact]
    public void Modulo_applies_to_a_vector_component_by_component()
    {
        // Arithmetic is component-wise with scalar promotion everywhere else, and there is no
        // reason for this one operator to be the exception.
        Scene scene = TestSource.LoadValid("sphere { center: [7, 8, 9] % 3 }");

        Assert.Equal(new Vector3(1f, 2f, 0f), Assert.IsType<Sphere>(scene.Roots[0]).Center);
    }

    [Fact]
    public void Boolean_operators_short_circuit()
    {
        // The right operand names nothing. Evaluating it would report 'unknown name', so a
        // load with no diagnostics is the proof that it was never evaluated.
        Scene scene = TestSource.LoadValid(
            """
            if (false && missingName) { sphere { } }
            if (true || missingName) { box { } }
            """);

        Assert.IsType<Box>(Assert.Single(scene.Roots));
    }

    [Fact]
    public void Rejects_a_condition_that_is_not_a_boolean()
    {
        // No truthiness. 'if (count)' has exactly one plausible reading and it is not one a
        // scene file should be allowed to rely on by accident.
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) =
            TestSource.Load("if (1) { sphere { } }");

        Assert.Null(scene);
        Assert.Contains(diagnostics, d => d.Message.Contains("condition must be true or false"));
    }

    [Fact]
    public void Rejects_comparing_values_of_different_kinds()
    {
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) =
            TestSource.Load("if (1 == \"one\") { sphere { } }");

        Assert.Null(scene);
        Assert.Contains(diagnostics, d => d.Message.Contains("cannot compare"));
    }

    [Fact]
    public void Rejects_ordering_anything_but_numbers()
    {
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) =
            TestSource.Load("if ([1, 2] < [3, 4]) { sphere { } }");

        Assert.Null(scene);
        Assert.Contains(diagnostics, d => d.Message.Contains("'<' compares numbers"));
    }

    [Fact]
    public void Rejects_arithmetic_on_booleans()
    {
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) =
            TestSource.Load("sphere { radius: true + 1 }");

        Assert.Null(scene);
        Assert.Contains(diagnostics, d => d.Message.Contains("booleans do not support arithmetic"));
    }

    [Fact]
    public void A_let_inside_a_block_is_visible_to_the_entries_around_it()
    {
        Scene scene = TestSource.LoadValid("sphere { let r = 3; radius: r, center: [r, 0, 0] }");

        Sphere sphere = Assert.IsType<Sphere>(scene.Roots[0]);
        Assert.Equal(3f, sphere.Radius);
        Assert.Equal(3f, sphere.Center.X);
    }

    [Fact]
    public void A_let_inside_a_block_does_not_escape_it()
    {
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) =
            TestSource.Load("sphere { let r = 3; radius: r }\nbox { min: [r, r, r], max: [1, 1, 1] }");

        Assert.Null(scene);
        Assert.Contains(diagnostics, d => d.Message.Contains("unknown name 'r'"));
    }

    [Fact]
    public void A_let_in_a_loop_body_is_fresh_each_iteration()
    {
        // The same 'let' runs three times. Without a frame per iteration the second one
        // collides with the first and the scene fails to load.
        Scene scene = TestSource.LoadValid(
            "for (let i = 0; i < 3; i++) { let r = i + 1; sphere { radius: r } }");

        Assert.Equal([1f, 2f, 3f], scene.Roots.Select(r => Assert.IsType<Sphere>(r).Radius));
    }

    [Fact]
    public void A_loop_counter_survives_between_iterations()
    {
        // The counter lives in the header's frame and the body's 'let' in the iteration's.
        // If the counter shared the body's frame, stepping it would step a copy and the loop
        // would never end — which the iteration budget would report and nothing else would.
        Scene scene = TestSource.LoadValid(
            "for (let i = 0; i < 3; i++) { sphere { radius: i + 1 } }");

        Assert.Equal([1f, 2f, 3f], scene.Roots.Select(r => Assert.IsType<Sphere>(r).Radius));
    }

    [Fact]
    public void A_loop_counter_does_not_escape_the_loop()
    {
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) =
            TestSource.Load("for (let i = 0; i < 2; i++) { }\nsphere { radius: i }");

        Assert.Null(scene);
        Assert.Contains(diagnostics, d => d.Message.Contains("unknown name 'i'"));
    }

    [Fact]
    public void A_loop_counter_may_not_shadow_an_existing_name()
    {
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) =
            TestSource.Load("let i = 1;\nfor (let i = 0; i < 3; i++) { sphere { } }");

        Assert.Null(scene);
        Assert.Contains(diagnostics, d => d.Message.Contains("'i' is already defined"));
    }

    [Fact]
    public void A_binding_may_be_assigned_to()
    {
        // Mutability came in with the loop, and it is the ordinary 'let' that carries it —
        // one rule rather than a special case for counters.
        Scene scene = TestSource.LoadValid(
            """
            let r = 1;
            for (let i = 0; i < 3; i++) { r = r * 2; }

            sphere { radius: r }
            """);

        Assert.Equal(8f, Assert.IsType<Sphere>(scene.Roots[0]).Radius);
    }

    [Fact]
    public void Assignment_never_declares()
    {
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) =
            TestSource.Load("r = 3\nsphere { radius: r }");

        Assert.Null(scene);
        Assert.Contains(diagnostics, d => d.Message.Contains("write 'let r = …' to declare it"));
    }

    [Fact]
    public void Rejects_stepping_something_that_is_not_a_number()
    {
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) =
            TestSource.Load("let c = [1, 2, 3];\nc++\nsphere { center: c }");

        Assert.Null(scene);
        Assert.Contains(diagnostics, d => d.Message.Contains("'++' steps a number"));
    }

    [Fact]
    public void Reports_a_field_written_at_the_top_level()
    {
        // Fields parse anywhere a statement does, now that a block and a file are the same
        // list. That makes this a binding mistake rather than a parse error, and the message
        // improves accordingly.
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) = TestSource.Load("radius: 3");

        Assert.Null(scene);
        Assert.Contains(diagnostics, d => d.Message.Contains("belongs inside a block"));
    }

    [Fact]
    public void Refuses_a_loop_that_would_run_away()
    {
        // The C-style header is not bounded by construction — this one has no condition at
        // all — so the budget is the only thing that ends it, and the diagnostic names the
        // loop rather than the thousandth sphere.
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) =
            TestSource.Load("for (;;) { sphere { } }");

        Assert.Null(scene);
        Assert.Contains(
            diagnostics,
            d => d.Message.Contains($"this loop has run {Evaluator.MaxLoopIterations} times"));

        // And it points at the 'for', not at the sphere the loop repeats.
        Assert.Equal("for", TestSource.TextAt(
            diagnostics.First(d => d.Message.Contains("has run"))));
    }

    [Fact]
    public void The_iteration_budget_is_shared_across_a_whole_load()
    {
        // Two loops of two thirds of the budget each. Neither exceeds it alone, which is the
        // point: the budget bounds the load, not the loop.
        int each = (Evaluator.MaxLoopIterations * 2) / 3;

        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) = TestSource.Load(
            $"for (let i = 0; i < {each}; i++) {{ }}\nfor (let j = 0; j < {each}; j++) {{ }}");

        Assert.Null(scene);
        Assert.Contains(diagnostics, d => d.Message.Contains("loop iterations in total"));
    }


    [Fact]
    public void The_lattice_deliverable_compiles_to_a_shader()
    {
        // scenes/lattice.chroma, structurally: 125 cells, each a union of one node and up to
        // three struts. It is here because the tape is what a loop actually strains — 850
        // instructions against the 256 that were plenty for seven iterations of hand-written
        // scenes — while the span budget, the thing the roadmap expected to give first,
        // barely moves: a ray crosses one cell at a time and four spans is the worst of it.
        CompiledScene compiled = TestSource.CompileValid(
            """
            let n = 5;

            for (let x = 0; x < n; x++) {
                for (let y = 0; y < n; y++) {
                    for (let z = 0; z < n; z++) {
                        union {
                            let p = ([x, y, z] - (n - 1) / 2) * 1.2;

                            sphere { center: p, radius: 0.3 }

                            if (x < n - 1) { cylinder { base: p, cap: p + [1.2, 0, 0], radius: 0.1 } }
                            if (y < n - 1) { cylinder { base: p, cap: p + [0, 1.2, 0], radius: 0.1 } }
                            if (z < n - 1) { cylinder { base: p, cap: p + [0, 0, 1.2], radius: 0.1 } }
                        }
                    }
                }
            }
            """);

        // 425 leaves in 125 cells, and eight shapes between them. A cell is a node plus up to
        // three struts, and which struts it has depends only on whether it is on a far face --
        // so there are seven distinct cells, plus the far corner, which has no struts at all and
        // is therefore a lone sphere standing on its own.
        //
        // The numbers below used to be 425 primitives and 125 functions. That they are not any
        // more is the whole of what instancing bought: what the driver compiles is the eight
        // distinct cells, and the other 124 placements are records in a buffer. Adding a
        // thousand more cells would not change either number.
        Assert.Equal(8, compiled.ShapeCount);
        Assert.Equal(124, compiled.InstanceCount);
        Assert.Equal(20, compiled.PrimitiveCount);
        Assert.Equal(4, compiled.WidestRoot);

        Assert.Equal(8, System.Text.RegularExpressions.Regex
            .Matches(compiled.Geometry, @"void shape\d+\(").Count);
    }

    [Fact]
    public void Comments_survive_everywhere_a_statement_can_appear()
    {
        // The two comment forms predate every iteration here. What is new is the places a
        // statement can now sit, and a comment inside a loop body must not swallow it.
        Scene scene = TestSource.LoadValid(
            """
            // a whole-line comment
            for (let i = 0; i < 2; i++) /* between the header and the body */ {
                // inside the body
                sphere { radius: 1 /* after a field */ }
            }
            """);

        Assert.Equal(2, scene.Roots.Count);
    }

    [Fact]
    public void The_loop_form_that_was_replaced_names_its_replacement()
    {
        // Every scene and every page of the reference used this until the revision, so a file
        // written against it is not a typo and deserves better than a cascade about '..'.
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) =
            TestSource.Load("for (i in 0..5) { sphere { } }");

        Assert.Null(scene);
        Assert.Contains(
            diagnostics,
            d => d.Message.Contains("write 'for (let i = a; i < b; i++)'"));
    }

    [Fact]
    public void A_file_in_the_old_syntax_gets_one_message_per_construct()
    {
        // Three replaced forms, and the point is the count as much as the wording: each is
        // read to its end and reported once, so a file being ported shows a list of what to
        // change rather than a cascade about the tokens the old forms were made of.
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) = TestSource.Load(
            """
            fn tile(x) = box { min: [x, 0, 0], max: [1, 1, 1] };
            for (i in 0..5) sphere { radius: i }
            sphere { radius: if (true) 1 else 2 }
            """);

        Assert.Null(scene);
        Assert.Equal(3, diagnostics.Count);

        Assert.Collection(
            diagnostics,
            d => Assert.Contains("write 'function tile(…) { return value; }'", d.Message),
            d => Assert.Contains("write 'for (let i = a; i < b; i++)'", d.Message),
            d => Assert.Contains("write 'condition ? a : b'", d.Message));
    }
}
