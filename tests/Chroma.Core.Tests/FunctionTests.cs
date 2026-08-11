using System.Numerics;
using Chroma.Core.Compilation;
using Chroma.Core.Model;
using Chroma.Core.Model.Geometry;
using Chroma.Core.Model.Geometry.Operations;
using Chroma.Core.Model.Geometry.Primitives;
using Chroma.Core.Sdl.Binding;
using Chroma.Core.Sdl.Source;

namespace Chroma.Core.Tests;

/// <summary>
/// <c>function</c> declarations and the calls that use them, and the <c>object</c> node
/// beside them.
/// </summary>
/// <remarks>
/// Written against the scene rather than against the syntax tree, for the reason
/// <see cref="ControlFlowTests"/> gives: a call produces a tree the parser never saw, so
/// asserting on the parse says nothing about what the file means.
/// </remarks>
public sealed class FunctionTests
{
    [Fact]
    public void A_function_returning_a_solid_is_instantiated_per_call()
    {
        Scene scene = TestSource.LoadValid(
            """
            function bead(x, r) {
                return sphere { center: [x, 0, 0], radius: r };
            }

            bead(-2, 0.5)
            bead(2, 1)
            """);

        Assert.Collection(
            scene.Roots,
            r => Assert.Equal(new Vector3(-2f, 0f, 0f), Assert.IsType<Sphere>(r).Center),
            r => Assert.Equal(1f, Assert.IsType<Sphere>(r).Radius));

        Assert.NotSame(scene.Roots[0], scene.Roots[1]);
    }

    [Fact]
    public void A_function_may_return_a_number_or_a_vector()
    {
        // Nothing about a function is solid-shaped. It returns a value, so whatever an
        // expression may produce, a function may return.
        Scene scene = TestSource.LoadValid(
            """
            function twice(x) { return x * 2; }
            function up(h) { return [0, h, 0]; }

            sphere { radius: twice(1.5), center: up(4) }
            """);

        Sphere sphere = Assert.IsType<Sphere>(scene.Roots[0]);
        Assert.Equal(3f, sphere.Radius);
        Assert.Equal(new Vector3(0f, 4f, 0f), sphere.Center);
    }

    [Fact]
    public void A_function_takes_no_arguments()
    {
        Scene scene = TestSource.LoadValid(
            "function one() { return sphere { radius: 1 }; }\none()");

        Assert.Equal(1f, Assert.IsType<Sphere>(Assert.Single(scene.Roots)).Radius);
    }

    [Fact]
    public void A_body_may_bind_branch_and_loop_before_it_returns()
    {
        // This is what the statement body buys over a named expression: the work leading to
        // the value is written in the function rather than folded into one expression.
        Scene scene = TestSource.LoadValid(
            """
            function stack(n) {
                let radius = 0.5;

                if (n > 2) { radius = 0.25; }

                return union {
                    for (let i = 0; i < n; i++) {
                        sphere { center: [0, i, 0], radius: radius }
                    }
                };
            }

            stack(3)
            """);

        Union union = Assert.IsType<Union>(Assert.Single(scene.Roots));

        Assert.Equal(3, union.Operands.Count);
        Assert.All(union.Operands, o => Assert.Equal(0.25f, Assert.IsType<Sphere>(o).Radius));
    }

    [Fact]
    public void A_return_ends_the_body_wherever_it_is_written()
    {
        // Inside an 'if', inside a loop: the value has to get past every enclosing statement
        // list on its way out, and the statements after it must not run.
        Scene scene = TestSource.LoadValid(
            """
            function firstOver(limit) {
                for (let i = 0; i < 100; i++) {
                    if (i * i > limit) { return sphere { radius: i }; }
                }

                return sphere { radius: 0 };
            }

            firstOver(30)
            """);

        Assert.Equal(6f, Assert.IsType<Sphere>(Assert.Single(scene.Roots)).Radius);
    }

    [Fact]
    public void A_function_body_sees_the_bindings_around_its_declaration()
    {
        Scene scene = TestSource.LoadValid(
            """
            let unit = 0.25;
            function bead(i) { return sphere { radius: unit * i }; }

            bead(4)
            """);

        Assert.Equal(1f, Assert.IsType<Sphere>(scene.Roots[0]).Radius);
    }

    [Fact]
    public void A_function_body_does_not_see_the_bindings_at_the_call_site()
    {
        // The closure is the scope the declaration sits in, not the one the call sits in, so
        // a function means the same thing wherever it is called from. That is 'include's rule
        // one level down, and the reason a fragment of helpers is safe to drop into a scene.
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) = TestSource.Load(
            """
            function bead(i) { return sphere { radius: i * hidden }; }

            union { let hidden = 2; bead(1) }
            """);

        Assert.Null(scene);
        Assert.Contains(diagnostics, d => d.Message.Contains("unknown name 'hidden'"));
    }

    [Fact]
    public void A_function_may_call_another()
    {
        Scene scene = TestSource.LoadValid(
            """
            function tinted(c) { return material { color: c, roughness: 0.2 }; }

            function bead(x) {
                return sphere { center: [x, 0, 0], material: tinted([1, 0, 0]) };
            }

            bead(3)
            """);

        Assert.Equal(new Vector3(1f, 0f, 0f), scene.Roots[0].Material!.Color);
    }

    [Fact]
    public void A_call_names_the_object_it_returns_after_the_function()
    {
        // Same courtesy a 'let' gets, and for the same reader: the hierarchy dump prints
        // 'material=tinted' rather than the material's components.
        Scene scene = TestSource.LoadValid(
            """
            function tinted(c) { return material { color: c }; }

            sphere { material: tinted([0, 1, 0]) }
            """);

        Assert.Equal("tinted", scene.Roots[0].Material!.Name);
    }

    [Fact]
    public void A_function_generates_geometry_inside_a_loop()
    {
        Scene scene = TestSource.LoadValid(
            """
            function strut(i) {
                return cylinder { base: [i, 0, 0], cap: [i, 1, 0], radius: 0.1 };
            }

            union {
                for (let i = 0; i < 4; i++) { strut(i) }
            }
            """);

        Assert.Equal(4, Assert.IsType<Union>(Assert.Single(scene.Roots)).Operands.Count);
    }

    [Fact]
    public void A_function_may_recurse()
    {
        // The closure is captured live, so the body can see the name being declared. Without
        // that a function could not call itself at all.
        Scene scene = TestSource.LoadValid(
            """
            function chain(n) {
                return union {
                    sphere { center: [0, n, 0], radius: 0.2 }
                    if (n > 0) { chain(n - 1) }
                };
            }

            chain(2)
            """);

        Union outer = Assert.IsType<Union>(Assert.Single(scene.Roots));
        Union middle = Assert.IsType<Union>(outer.Operands[1]);
        Union inner = Assert.IsType<Union>(middle.Operands[1]);

        Assert.Equal(new Vector3(0f, 0f, 0f), Assert.IsType<Sphere>(inner.Operands[0]).Center);
        Assert.Single(inner.Operands);
    }

    [Fact]
    public void An_argument_is_evaluated_in_the_caller_s_scope()
    {
        // The parameter and the caller's binding share a name. The argument is evaluated
        // before the frame that will hold the parameter exists, so it reads the caller's 3
        // rather than looping back on itself.
        Scene scene = TestSource.LoadValid(
            """
            function scaled(r) { return sphere { radius: r * 2 }; }

            union { let r = 3; scaled(r) }
            """);

        Union union = Assert.IsType<Union>(scene.Roots[0]);
        Assert.Equal(6f, Assert.IsType<Sphere>(union.Operands[0]).Radius);
    }

    [Fact]
    public void A_parameter_may_be_assigned_to_without_touching_the_caller()
    {
        // The parameter is a binding in the call's own frame, so stepping it inside the body
        // cannot reach the caller's value. Anything else would make a call site depend on
        // what the function does with what it is given.
        Scene scene = TestSource.LoadValid(
            """
            function grown(r) {
                r = r + 10;
                return sphere { radius: r };
            }

            let start = 1;
            grown(start)
            sphere { radius: start }
            """);

        Assert.Equal(11f, Assert.IsType<Sphere>(scene.Roots[0]).Radius);
        Assert.Equal(1f, Assert.IsType<Sphere>(scene.Roots[1]).Radius);
    }

    [Fact]
    public void Reports_a_body_that_falls_off_the_end()
    {
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) = TestSource.Load(
            """
            function bead(r) { sphere { radius: r } }

            union { for (let i = 0; i < 5; i++) { bead(i) } }
            """);

        Assert.Null(scene);
        Assert.Contains(
            diagnostics,
            d => d.Message.Contains("'bead' reaches the end of its body without a 'return'"));

        // Once, not once per call: five iterations must not produce five copies of it.
        Assert.Single(diagnostics, d => d.Message.Contains("without a 'return'"));
    }

    [Fact]
    public void Reports_a_value_a_body_produces_but_does_not_return()
    {
        // The mistake the message is for: a solid written in a body without 'return' in front
        // of it, which reads exactly like a scene file everywhere else in the language.
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) = TestSource.Load(
            """
            function bead(r) {
                sphere { radius: r }
                return box { };
            }

            bead(1)
            """);

        Assert.Null(scene);
        Assert.Contains(
            diagnostics,
            d => d.Message.Contains("this value is not used; 'bead' produces its result with 'return'"));
    }

    [Fact]
    public void Reports_a_return_outside_a_function()
    {
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) =
            TestSource.Load("return sphere { };");

        Assert.Null(scene);
        Assert.Contains(diagnostics, d => d.Message.Contains("a 'return' belongs inside a function"));
    }

    [Fact]
    public void Reports_a_call_with_the_wrong_number_of_arguments()
    {
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) = TestSource.Load(
            "function bead(x, r) { return sphere { radius: r }; }\nbead(1)");

        Assert.Null(scene);
        Assert.Contains(diagnostics, d => d.Message.Contains("'bead' takes 2 arguments, found 1"));
    }

    [Fact]
    public void Reports_a_call_to_a_name_that_is_not_a_function()
    {
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) =
            TestSource.Load("let r = 2;\nsphere { radius: r(1) }");

        Assert.Null(scene);
        Assert.Contains(diagnostics, d => d.Message.Contains("'r' is a number and cannot be called"));
    }

    [Fact]
    public void Reports_a_call_to_an_unknown_name()
    {
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) =
            TestSource.Load("sphere { radius: missing(1) }");

        Assert.Null(scene);
        Assert.Contains(diagnostics, d => d.Message.Contains("unknown function 'missing'"));
    }

    [Fact]
    public void A_function_may_not_shadow_an_existing_name()
    {
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) =
            TestSource.Load("let bead = 1;\nfunction bead(x) { return x; }");

        Assert.Null(scene);
        Assert.Contains(diagnostics, d => d.Message.Contains("'bead' is already defined"));
    }

    [Fact]
    public void A_parameter_may_not_shadow_an_existing_name()
    {
        // Reported at the declaration rather than at each call, because the declaration is
        // where the mistake is written and reporting it once is enough.
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) = TestSource.Load(
            """
            let r = 1;
            function bead(r) { return sphere { radius: r }; }

            bead(2)
            bead(3)
            """);

        Assert.Null(scene);

        Diagnostic shadowed = Assert.Single(diagnostics);
        Assert.Contains("'r' is already defined", shadowed.Message);
    }

    [Fact]
    public void Reports_a_parameter_declared_twice()
    {
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) =
            TestSource.Load("function bead(r, r) { return sphere { radius: r }; }");

        Assert.Null(scene);
        Assert.Contains(
            diagnostics, d => d.Message.Contains("'r' is already a parameter of 'bead'"));
    }

    [Fact]
    public void Refuses_a_recursion_that_never_ends()
    {
        // The counterpart of the loop budget, and the reason it cannot be the loop budget:
        // the evaluator recurses on the CLR stack, and a stack overflow takes the process
        // down with no diagnostic at all.
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) = TestSource.Load(
            """
            function forever(n) { return forever(n + 1); }

            sphere { radius: forever(0) }
            """);

        Assert.Null(scene);
        Assert.Contains(
            diagnostics,
            d => d.Message.Contains($"'forever' is called {Evaluator.MaxCallDepth} calls deep"));

        // Once, however many calls meet the limit. A recursion that branches would otherwise
        // report it thousands of times over.
        Assert.Single(diagnostics, d => d.Message.Contains("calls deep"));
    }

    [Fact]
    public void Refuses_a_recursion_that_branches_faster_than_it_ends()
    {
        // Within the depth limit, and 2^40 calls. Depth alone does not bound the work, which
        // is why there are two budgets rather than one.
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) = TestSource.Load(
            """
            function tree(n) {
                if (n == 0) { return 1; }
                return tree(n - 1) + tree(n - 1);
            }

            sphere { radius: tree(40) }
            """);

        Assert.Null(scene);
        Assert.Contains(
            diagnostics,
            d => d.Message.Contains($"may make {Evaluator.MaxFunctionCalls} function calls"));
    }

    [Fact]
    public void An_included_fragment_exports_its_functions()
    {
        // Functions are ordinary values in the ordinary scope, so the export rule 'include'
        // already had applies to them with nothing added.
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) = TestSource.LoadFiles(
            "scene.chroma",
            ("scene.chroma",
                TestSource.Camera + "include \"parts.chroma\";\nbead(2)"),
            ("parts.chroma", "function bead(r) { return sphere { radius: r }; }"));

        Assert.NotNull(scene);
        Assert.Empty(diagnostics);
        Assert.Equal(2f, Assert.IsType<Sphere>(Assert.Single(scene.Roots)).Radius);
    }

    [Fact]
    public void A_fragment_s_function_keeps_its_own_bindings()
    {
        // The fragment's 'let' is private to it and the includer never sees it, but the
        // function declared beside it still does — the closure is the fragment's scope.
        (Scene? scene, _) = TestSource.LoadFiles(
            "scene.chroma",
            ("scene.chroma", TestSource.Camera + "include \"parts.chroma\";\nbead(4)"),
            ("parts.chroma",
                "let unit = 0.25;\nfunction bead(i) { return sphere { radius: unit * i }; }"));

        Assert.NotNull(scene);
        Assert.Equal(1f, Assert.IsType<Sphere>(Assert.Single(scene.Roots)).Radius);
    }

    [Fact]
    public void An_object_carries_the_modifiers_of_the_solid_it_wraps()
    {
        // The point of the node: a reference on its own takes no modifiers, and this is
        // where they go.
        Scene scene = TestSource.LoadValid(
            """
            let unit = box { min: [-1, -1, -1], max: [1, 1, 1] };

            object {
                unit
                translate: [0, 2, 0]
                material: material { color: [0.75, 0.76, 0.8] }
            }
            """);

        Solid wrapper = Assert.Single(scene.Roots);
        Assert.Equal(new Vector3(0f, 2f, 0f), Assert.Single(wrapper.Transform.Steps).Value);
        Assert.Equal(new Vector3(0.75f, 0.76f, 0.8f), wrapper.Material!.Color);

        // A union of one operand is that operand, which is exactly what the node means.
        Assert.IsType<Box>(Assert.Single(Assert.IsType<Union>(wrapper).Operands));
    }

    [Fact]
    public void An_object_wrapper_generates_no_code_of_its_own()
    {
        // n operands binarise into n - 1 operators, so a single operand emits none at all.
        // The wrapper is free, which is what makes it a naming decision rather than a
        // rendering one — and with the tree generated, "free" is checkable directly: the two
        // scenes differ only by the translation baked into the leaf's matrix.
        CompiledScene bare = TestSource.CompileValid("sphere { radius: 1 }");

        CompiledScene wrapped =
            TestSource.CompileValid("object { sphere { radius: 1 }, translate: [0, 1, 0] }");

        Assert.Equal(bare.WidestRoot, wrapped.WidestRoot);
        Assert.Equal(bare.GeneratedLines, wrapped.GeneratedLines);
    }

    [Theory]
    [InlineData("object { }", "found 0")]
    [InlineData("object { sphere { } box { } }", "found 2; use 'union' to combine several")]
    public void An_object_wraps_exactly_one_solid(string source, string expected)
    {
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) = TestSource.Load(source);

        Assert.Null(scene);
        Assert.Contains(
            diagnostics,
            d => d.Message.Contains("'object' wraps exactly one solid") && d.Message.Contains(expected));
    }
}
