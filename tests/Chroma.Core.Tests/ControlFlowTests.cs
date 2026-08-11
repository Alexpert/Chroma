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
/// Conditions and loops: the iteration 8 revision, observed through the scene it builds.
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
        Scene scene = TestSource.LoadValid("for (i in 0..4) sphere { center: [i, 0, 0] }");

        Assert.Equal(4, scene.Roots.Count);
        Assert.Equal(
            [0f, 1f, 2f, 3f],
            scene.Roots.Select(r => Assert.IsType<Sphere>(r).Center.X));
    }

    [Fact]
    public void A_range_is_half_open()
    {
        // 0..n runs n times, the way every 'for i in range(n)' a reader has met does. The
        // alternative reads better in 'for (i in 1..12)' and worse everywhere else.
        Scene scene = TestSource.LoadValid("for (i in 2..5) sphere { radius: i }");

        Assert.Equal([2f, 3f, 4f], scene.Roots.Select(r => Assert.IsType<Sphere>(r).Radius));
    }

    [Theory]
    [InlineData("0..0")]
    [InlineData("5..0")]
    public void An_empty_or_backwards_range_emits_nothing(string range)
    {
        // Not an error: 'for (i in 0..n)' with n = 0 is the ordinary way to write "none of
        // these", and refusing it would put a guard around every generated count.
        Scene scene = TestSource.LoadValid($"sphere {{ }} for (i in {range}) box {{ }}");

        Assert.IsType<Sphere>(Assert.Single(scene.Roots));
    }

    [Fact]
    public void Nested_loops_multiply()
    {
        Scene scene = TestSource.LoadValid(
            "for (x in 0..3) for (y in 0..2) sphere { center: [x, y, 0] }");

        Assert.Equal(6, scene.Roots.Count);
        Assert.Equal(
            new Vector3(2f, 1f, 0f),
            Assert.IsType<Sphere>(scene.Roots[^1]).Center);
    }

    [Fact]
    public void A_loop_generates_the_operands_of_an_operator()
    {
        Scene scene = TestSource.LoadValid(
            "union { for (i in 0..3) sphere { center: [i * 3, 0, 0], radius: 1 } }");

        Union union = Assert.IsType<Union>(Assert.Single(scene.Roots));
        Assert.Equal(3, union.Operands.Count);
    }

    [Fact]
    public void An_if_decides_whether_an_entry_exists()
    {
        Scene scene = TestSource.LoadValid(
            "for (i in 0..4) if (i < 2) sphere { radius: i }");

        Assert.Equal(2, scene.Roots.Count);
    }

    [Fact]
    public void An_if_chooses_between_two_bodies()
    {
        Scene scene = TestSource.LoadValid(
            "for (i in 0..3) { if (i == 1) { box { } } else { sphere { } } }");

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
            "for (i in 0..3) { if (i == 0) { box { } } else if (i == 1) { sphere { } } else { plane { } } }");

        Assert.Collection(
            scene.Roots,
            r => Assert.IsType<Box>(r),
            r => Assert.IsType<Sphere>(r),
            r => Assert.IsType<Plane>(r));
    }

    [Fact]
    public void An_if_without_braces_is_a_value_and_needs_an_else()
    {
        // The message has to name the fix, because the mistake is almost always someone
        // reaching for the statement form and leaving the braces off.
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) =
            TestSource.Load("let r = if (true) 1;\nsphere { radius: r }");

        Assert.Null(scene);
        Assert.Contains(diagnostics, d => d.Message.Contains("needs an 'else'"));
    }

    [Fact]
    public void An_if_expression_chooses_between_two_values()
    {
        Scene scene = TestSource.LoadValid(
            "let red = material { color: [1, 0, 0] };\n"
            + "let blue = material { color: [0, 0, 1] };\n"
            + "for (i in 0..2) sphere { material: if (i == 0) red else blue }");

        Assert.Equal(new Vector3(1f, 0f, 0f), scene.Roots[0].Material!.Color);
        Assert.Equal(new Vector3(0f, 0f, 1f), scene.Roots[1].Material!.Color);
    }

    [Fact]
    public void Only_the_branch_taken_is_evaluated()
    {
        // The untaken branch names something that does not exist. If it were evaluated the
        // load would fail, and a scene could not use 'if' to guard a value at all.
        Scene scene = TestSource.LoadValid("sphere { radius: if (true) 2 else missingName }");

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
    public void Evaluates_comparisons_and_boolean_operators(string expression, bool expected)
    {
        Scene scene = TestSource.LoadValid($"if ({expression}) sphere {{ }}");

        Assert.Equal(expected, scene.Roots.Count == 1);
    }

    [Fact]
    public void Boolean_operators_short_circuit()
    {
        // The right operand names nothing. Evaluating it would report 'unknown name', so a
        // load with no diagnostics is the proof that it was never evaluated.
        Scene scene = TestSource.LoadValid(
            "if (false && missingName) sphere { }\n"
            + "if (true || missingName) box { }");

        Assert.IsType<Box>(Assert.Single(scene.Roots));
    }

    [Fact]
    public void Rejects_a_condition_that_is_not_a_boolean()
    {
        // No truthiness. 'if (count)' has exactly one plausible reading and it is not one a
        // scene file should be allowed to rely on by accident.
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) =
            TestSource.Load("if (1) sphere { }");

        Assert.Null(scene);
        Assert.Contains(diagnostics, d => d.Message.Contains("condition must be true or false"));
    }

    [Fact]
    public void Rejects_comparing_values_of_different_kinds()
    {
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) =
            TestSource.Load("if (1 == \"one\") sphere { }");

        Assert.Null(scene);
        Assert.Contains(diagnostics, d => d.Message.Contains("cannot compare"));
    }

    [Fact]
    public void Rejects_ordering_anything_but_numbers()
    {
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) =
            TestSource.Load("if ([1, 2] < [3, 4]) sphere { }");

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
    public void Rejects_a_range_bound_that_is_not_a_whole_number()
    {
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) =
            TestSource.Load("for (i in 0..2.5) sphere { }");

        Assert.Null(scene);
        Assert.Contains(diagnostics, d => d.Message.Contains("must be a whole number"));
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
        Scene scene = TestSource.LoadValid("for (i in 0..3) { let r = i + 1; sphere { radius: r } }");

        Assert.Equal([1f, 2f, 3f], scene.Roots.Select(r => Assert.IsType<Sphere>(r).Radius));
    }

    [Fact]
    public void A_loop_variable_may_not_shadow_an_existing_name()
    {
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) =
            TestSource.Load("let i = 1;\nfor (i in 0..3) sphere { }");

        Assert.Null(scene);
        Assert.Contains(diagnostics, d => d.Message.Contains("'i' is already defined"));
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
        // 'for' cannot loop forever, but it can loop for an hour, and a loader that
        // disappears reports nothing at all. The diagnostic names the loop and its count.
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) =
            TestSource.Load($"for (i in 0..{Evaluator.MaxLoopIterations + 1}) sphere {{ }}");

        Assert.Null(scene);
        Assert.Contains(
            diagnostics,
            d => d.Message.Contains($"loop over 'i' runs {Evaluator.MaxLoopIterations + 1} times"));
    }

    [Fact]
    public void A_span_budget_overflow_names_the_loop_rather_than_the_last_sphere()
    {
        // Nine disjoint spheres under one 'union' is nine spans along a ray and the shader
        // holds eight. The operator is written correctly; the count is what has to change,
        // so that is what the diagnostic points at.
        int count = GpuLayout.MaxSpans + 1;

        (CompiledScene? compiled, IReadOnlyList<Diagnostic> diagnostics) = TestSource.Compile(
            $"union {{ for (i in 0..{count}) sphere {{ center: [i * 3, 0, 0], radius: 1 }} }}");

        Assert.Null(compiled);

        Diagnostic overflow = Assert.Single(diagnostics);
        Assert.Contains($"loop over 'i' puts {count} solids in a 'union'", overflow.Message);
        Assert.Contains($"the shader holds {GpuLayout.MaxSpans}", overflow.Message);

        // And it points at the 'for', not at the 'sphere' the loop repeats.
        Assert.Equal("for", TestSource.TextAt(overflow));
    }

    [Fact]
    public void A_hand_written_overflow_still_names_the_operator()
    {
        // The loop-aware message must not replace the original one. Nothing generated this
        // union's operands, so there is no loop to blame.
        string spheres = string.Join(
            "\n", Enumerable.Range(0, GpuLayout.MaxSpans + 1).Select(i => $"sphere {{ center: [{i * 3}, 0, 0] }}"));

        (CompiledScene? compiled, IReadOnlyList<Diagnostic> diagnostics) =
            TestSource.Compile($"union {{\n{spheres}\n}}");

        Assert.Null(compiled);
        Assert.Contains(diagnostics, d => d.Message.Contains("this 'union' can produce up to"));
    }

    [Fact]
    public void The_lattice_deliverable_compiles_within_every_budget()
    {
        // scenes/lattice.chroma, structurally: 125 cells, each a union of one node and up to
        // three struts. It is here because the tape is what a loop actually strains — 850
        // instructions against the 256 that were plenty for seven iterations of hand-written
        // scenes — while the span budget, the thing the roadmap expected to give first,
        // barely moves: a ray crosses one cell at a time and four spans is the worst of it.
        CompiledScene compiled = TestSource.CompileValid(
            """
            let n = 5;
            for (x in 0..n) for (y in 0..n) for (z in 0..n) union {
                let p = ([x, y, z] - (n - 1) / 2) * 1.2;

                sphere { center: p, radius: 0.3 }

                if (x < n - 1) cylinder { base: p, cap: p + [1.2, 0, 0], radius: 0.1 }
                if (y < n - 1) cylinder { base: p, cap: p + [0, 1.2, 0], radius: 0.1 }
                if (z < n - 1) cylinder { base: p, cap: p + [0, 0, 1.2], radius: 0.1 }
            }
            """);

        // 425 leaves + 300 union operators + 125 end-of-root markers, plus one bounding-box
        // guard per cell. The guards are why this scene renders in a tenth of the time it used
        // to: a ray meets one cell and skips the other 124 without evaluating a primitive.
        // They are emitted because the scene is over CsgTapeBuilder.GuardsPayFrom — a
        // hand-written scene of the same shape but a twentieth the size gets none.
        Assert.Equal(850 + 125, compiled.InstructionCount);
        Assert.Equal(425, compiled.PrimitiveCount);
        Assert.True(compiled.HasBounds);

        Assert.Equal(4, compiled.Budget.Spans);
        Assert.True(compiled.Budget.StackDepth <= GpuLayout.MaxStackDepth);
    }

    [Fact]
    public void Comments_survive_everywhere_a_statement_can_appear()
    {
        // The two comment forms predate every iteration here. What is new is the places a
        // statement can now sit, and a comment inside a loop body must not swallow it.
        Scene scene = TestSource.LoadValid(
            """
            // a whole-line comment
            for (i in 0..2) /* between the header and the body */ {
                // inside the body
                sphere { radius: 1 /* after a field */ }
            }
            """);

        Assert.Equal(2, scene.Roots.Count);
    }
}
