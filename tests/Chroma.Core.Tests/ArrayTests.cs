using System.Numerics;
using Chroma.Core.Model;
using Chroma.Core.Model.Geometry.Operations;
using Chroma.Core.Model.Geometry.Primitives;
using Chroma.Core.Sdl.Source;

namespace Chroma.Core.Tests;

/// <summary>
/// Arrays: the language's vector, widened to hold values of any kind and to nest.
/// </summary>
public sealed class ArrayTests
{
    private static float Radius(string body, string expression) =>
        Assert.IsType<Sphere>(
            TestSource.LoadValid($"{body}\nsphere {{ radius: {expression} }}").Roots[0]).Radius;

    [Fact]
    public void An_array_of_numbers_is_still_the_vector_it_was()
    {
        // The widening is not a second kind beside the old one. Every scene written before it
        // means what it meant, arithmetic included.
        Scene scene = TestSource.LoadValid("sphere { center: [1, 2, 3] * 2 + [0, 1, 0] }");

        Assert.Equal(new Vector3(2f, 5f, 6f), Assert.IsType<Sphere>(scene.Roots[0]).Center);
    }

    [Fact]
    public void Reads_an_element_by_index()
    {
        Assert.Equal(7f, Radius("let a = [5, 7, 9];", "a[1]"));
    }

    [Fact]
    public void Reports_the_length()
    {
        Assert.Equal(3f, Radius("let a = [5, 7, 9];", "a.length"));
    }

    [Fact]
    public void Nests()
    {
        Assert.Equal(4f, Radius("let grid = [[1, 2], [3, 4]];", "grid[1][1]"));
    }

    [Fact]
    public void Holds_values_of_different_kinds_at_once()
    {
        // Nothing requires an array to be uniform. What requires numbers is the field that
        // reads one, and it says so where it reads it.
        Assert.Equal(2f, Radius("let mixed = [1, \"bezier\", true, [2]];", "mixed[3][0]"));
    }

    [Fact]
    public void Holds_nodes_and_places_them_from_an_index()
    {
        Scene scene = TestSource.LoadValid(
            "let shapes = [sphere { radius: 3 }, box { }];\n"
            + "object { shapes[0] }\n"
            + "object { shapes[1] }");

        Assert.Equal(3f, Assert.IsType<Sphere>(Assert.IsType<Union>(scene.Roots[0]).Operands[0]).Radius);
        Assert.IsType<Box>(Assert.IsType<Union>(scene.Roots[1]).Operands[0]);
    }

    [Fact]
    public void An_element_holding_a_node_instantiates_it_at_each_use()
    {
        // The rule a binding already had, reaching through an array: two placements of one
        // element are two independent solids, not one solid in two places.
        Scene scene = TestSource.LoadValid(
            "let shapes = [sphere { radius: 3 }];\n"
            + "object { shapes[0], translate: [-2, 0, 0] }\n"
            + "object { shapes[0], translate: [ 2, 0, 0] }");

        Assert.NotSame(
            Assert.IsType<Union>(scene.Roots[0]).Operands[0],
            Assert.IsType<Union>(scene.Roots[1]).Operands[0]);
    }

    [Fact]
    public void Is_walked_by_the_loop_the_language_already_had()
    {
        Scene scene = TestSource.LoadValid(
            "let radii = [1, 2, 3];\n"
            + "for (let i = 0; i < radii.length; i++) { sphere { radius: radii[i] } }");

        Assert.Equal(
            [1f, 2f, 3f],
            scene.Roots.Select(r => Assert.IsType<Sphere>(r).Radius));
    }

    [Fact]
    public void Is_passed_to_and_returned_from_a_function()
    {
        Assert.Equal(
            9f,
            Radius("function third(a) { return [a[0], a[1], 9]; }", "third([1, 2, 3])[2]"));
    }

    [Theory]

    // Same length, compared element by element, and nesting is followed.
    [InlineData("[1, 2] == [1, 2]", true)]
    [InlineData("[1, 2] == [1, 3]", false)]
    [InlineData("[[1, 2]] == [[1, 2]]", true)]

    // A different length is unequal rather than incomparable: two arrays are the same kind,
    // and a length is a fact about a value rather than about its type.
    [InlineData("[1, 2] == [1, 2, 3]", false)]
    public void Compares_element_by_element(string expression, bool expected)
    {
        Assert.Equal(expected ? 1f : 2f, Radius(string.Empty, $"({expression}) ? 1 : 2"));
    }

    [Theory]
    [InlineData("a[3]", "index 3 is out of range; the array has 3 elements, so 0 to 2")]
    [InlineData("a[-1]", "index -1 is out of range")]
    [InlineData("a[0.5]", "an index must be a whole number, found 0.5")]
    [InlineData("a[true]", "an index must be a number, found the boolean true")]
    public void Reports_an_index_that_cannot_be_read(string expression, string message)
    {
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) =
            TestSource.Load($"let a = [1, 2, 3];\nsphere {{ radius: {expression} }}");

        Assert.Null(scene);
        Assert.Contains(diagnostics, d => d.Message.Contains(message));
    }

    [Fact]
    public void Reports_indexing_something_that_is_not_an_array()
    {
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) =
            TestSource.Load("let n = 3;\nsphere { radius: n[0] }");

        Assert.Null(scene);
        Assert.Contains(diagnostics, d => d.Message.Contains("cannot index a number"));
    }

    [Fact]
    public void Reports_a_member_other_than_length()
    {
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) =
            TestSource.Load("let a = [1, 2];\nsphere { radius: a.count }");

        Assert.Null(scene);
        Assert.Contains(
            diagnostics, d => d.Message.Contains("'length' is the only one it has"));
    }

    [Fact]
    public void Refuses_arithmetic_on_an_array_that_nests()
    {
        // Refused as a whole rather than element by element: a list of points has no
        // arithmetic, and a deeper broadcast is not the answer anyone wanted.
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) =
            TestSource.Load("sphere { radius: [[1, 2], [3, 4]] * 2 }");

        Assert.Null(scene);
        Assert.Contains(
            diagnostics,
            d => d.Message.Contains("arithmetic needs an array of numbers, found an array of 2 elements"));
    }

    [Fact]
    public void Assigns_to_an_element()
    {
        Assert.Equal(5f, Radius("let a = [1, 2];\na[0] = 5;", "a[0]"));
    }

    [Fact]
    public void Assigns_through_a_nested_element()
    {
        Assert.Equal(7f, Radius("let grid = [[1, 2], [3, 4]];\ngrid[1][0] = 7;", "grid[1][0]"));
    }

    [Fact]
    public void An_assignment_to_an_element_is_invisible_to_every_other_binding()
    {
        // Assigning rebuilds the array and rebinds the name rather than mutating anything, so
        // an array is still a value and nothing else can observe the write.
        Assert.Equal(1f, Radius("let a = [1, 2];\nlet b = a;\nb[0] = 99;", "a[0]"));
    }

    [Theory]
    [InlineData("a[5] = 1", "out of range")]
    [InlineData("a.length = 4", "'length' is the only one it has, and it is not a field")]
    public void Reports_an_assignment_it_cannot_make(string statement, string message)
    {
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) =
            TestSource.Load($"let a = [1, 2];\n{statement};\nsphere {{ }}");

        Assert.Null(scene);
        Assert.Contains(diagnostics, d => d.Message.Contains(message));
    }

    [Fact]
    public void Reports_a_step_on_an_element_and_names_the_way_round_it()
    {
        // '++' steps a name, and widening it to a path would mean deciding how many times the
        // index between the brackets is evaluated.
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) =
            TestSource.Load("let a = [1, 2];\na[0]++\nsphere { }");

        Assert.Null(scene);
        Assert.Contains(diagnostics, d => d.Message.Contains("write 'a[0] = a[0] + 1'"));
    }

    [Fact]
    public void Splices_an_array_written_as_a_child()
    {
        // The asymmetry with a field, which keeps what it is given: a child position means
        // "a thing that belongs here", and a list of those belongs here.
        Scene scene = TestSource.LoadValid(
            "let shapes = [sphere { radius: 1 }, box { }];\nunion { shapes }");

        Assert.Equal(2, Assert.IsType<Union>(scene.Roots[0]).Operands.Count);
    }

    [Fact]
    public void Splices_all_the_way_down()
    {
        // An array was never a valid child on its own, so there is no case where leaving a
        // nested one unspliced would have been what a file meant.
        Scene scene = TestSource.LoadValid(
            "let row = [sphere { radius: 1 }, sphere { radius: 2 }];\nunion { [row, row] }");

        Assert.Equal(4, Assert.IsType<Union>(scene.Roots[0]).Operands.Count);
    }

    [Fact]
    public void An_empty_array_as_a_child_contributes_nothing()
    {
        Scene scene = TestSource.LoadValid("union { sphere { }, [] }");

        Assert.Single(Assert.IsType<Union>(scene.Roots[0]).Operands);
    }

    [Fact]
    public void A_field_holding_an_array_keeps_it_whole()
    {
        // The other half of the same rule. A field has a name and a declared meaning, so
        // 'points' is one list and has to stay one.
        Scene scene = TestSource.LoadValid("prism { points: [[0, 0], [1, 0], [1, 1]] bottom: 0 top: 1 }");

        Assert.Equal(3, Assert.IsType<Prism>(scene.Roots[0]).Points.Count);
    }

    [Fact]
    public void An_array_literal_beside_another_is_two_arrays_and_not_an_index()
    {
        // Commas are optional everywhere in this language, so this is the case a naive postfix
        // '[' would read as one array indexed by another. Two points, and the prism says so.
        Scene scene = TestSource.LoadValid(
            "prism { points: [[0, 0] [1, 0] [1, 1]] bottom: 0 top: 1 }");

        Assert.Equal(3, Assert.IsType<Prism>(scene.Roots[0]).Points.Count);
    }

    [Fact]
    public void A_child_followed_by_an_array_literal_is_two_statements()
    {
        // The same guard, at statement level: without it this would read as indexing the
        // sphere, and the message would be about the wrong thing entirely.
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) =
            TestSource.Load("sphere { radius: 1 }\n[1, 2, 3]");

        Assert.Null(scene);

        // Three complaints, one per spliced element, rather than one about a sphere that
        // cannot be indexed. The count is the proof that the array was read as its own
        // statement: indexing would have produced a single message about the sphere.
        Assert.Equal(3, diagnostics.Count);
        Assert.All(
            diagnostics,
            d => Assert.Equal("expected an object, found a number", d.Message));
    }

    [Theory]

    // Half-open, which is what '0..n' meant in the loop form this language used to have and
    // what every 'range(n)' a reader has met elsewhere gives.
    [InlineData("[0..5].length", 5f)]
    [InlineData("r[0]", 0f)]
    [InlineData("r[4]", 4f)]
    [InlineData("[-2..2].length", 4f)]

    // Empty rather than counting down, for the same reason a 'for' written that way runs no
    // iterations.
    [InlineData("[2..2].length", 0f)]
    [InlineData("[5..0].length", 0f)]

    // The bounds are expressions like any other.
    [InlineData("[n..n + 3].length", 3f)]
    public void A_range_counts_up_to_its_end_without_reaching_it(string expression, float expected)
    {
        Assert.Equal(expected, Radius("let n = 2;\nlet r = [0..5];", expression));
    }

    [Fact]
    public void A_range_is_an_ordinary_array()
    {
        // Nothing downstream can tell which spelling made it: it walks, splices and nests like
        // any other array.
        Scene scene = TestSource.LoadValid(
            "let radii = [1..4];\n"
            + "for (let i = 0; i < radii.length; i++) { sphere { radius: radii[i] } }");

        Assert.Equal([1f, 2f, 3f], scene.Roots.Select(r => Assert.IsType<Sphere>(r).Radius));
    }

    [Theory]
    [InlineData("[0.5..3]", "a range bound must be a whole number, found 0.5")]
    [InlineData("[0..2.5]", "a range bound must be a whole number, found 2.5")]
    [InlineData("[true..3]", "a range bound must be a number, found the boolean true")]
    [InlineData("[0..1 / 0]", "a range bound must be a whole number")]
    [InlineData("[1, 0..3]", "a range is the whole of an array literal; write '[a..b]'")]
    public void Reports_a_range_it_cannot_read(string expression, string message)
    {
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) =
            TestSource.Load($"sphere {{ radius: {expression}.length }}");

        Assert.Null(scene);
        Assert.Contains(diagnostics, d => d.Message.Contains(message));
    }

    [Theory]
    [InlineData("array(5, 0).length", 5f)]
    [InlineData("array(5, 0)[4]", 0f)]
    [InlineData("array(0, 0).length", 0f)]

    // Anything at all in the second place, so there is no second name for the version that
    // repeats something other than a number.
    [InlineData("array(3, [7, 8])[2][1]", 8f)]
    [InlineData("array(2, [1..4])[1].length", 3f)]
    public void Array_repeats_one_value(string expression, float expected)
    {
        Assert.Equal(expected, Radius(string.Empty, expression));
    }

    [Fact]
    public void Array_is_filled_by_index_afterwards()
    {
        // What the built-in is for: a length the literal cannot give when the count is a
        // variable, filled by the loop that knows what belongs in it.
        Assert.Equal(
            6f,
            Radius(
                "let n = 4;\n"
                + "let heights = array(n, 0);\n"
                + "for (let i = 0; i < n; i++) { heights[i] = i * 2; }",
                "heights[3]"));
    }

    [Theory]
    [InlineData("array(2.5, 0)", "'n' of 'array' must be a whole number, found 2.5")]
    [InlineData("array(-1, 0)", "'n' of 'array' must not be negative, found -1")]
    [InlineData("array(3)", "'array' takes 2 arguments, found 1")]
    [InlineData("array([1, 2], 0)", "'n' of 'array' is a number, found a vector of 2 components")]
    public void Reports_an_array_it_cannot_build(string expression, string message)
    {
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) =
            TestSource.Load($"sphere {{ radius: {expression}.length }}");

        Assert.Null(scene);
        Assert.Contains(diagnostics, d => d.Message.Contains(message));
    }

    [Fact]
    public void Push_lengthens_an_array()
    {
        Assert.Equal(3f, Radius("let a = [1, 2];\na.push(9);", "a.length"));
    }

    [Fact]
    public void Push_accumulates_the_shapes_a_loop_makes()
    {
        // The reason it exists. Before it, a list whose length is not known where it is
        // written could not be built at all.
        Scene scene = TestSource.LoadValid(
            """
            let shapes = [];

            for (let i = 0; i < 4; i++) {
              if (i != 2) { shapes.push(sphere { radius: i + 1 }); }
            }

            union { shapes }
            """);

        Assert.Equal(
            [1f, 2f, 4f],
            Assert.IsType<Union>(scene.Roots[0]).Operands.Select(o => Assert.IsType<Sphere>(o).Radius));
    }

    [Fact]
    public void Push_reaches_through_a_path()
    {
        Assert.Equal(3f, Radius("let grid = [[1], [2, 3]];\ngrid[1].push(5);", "grid[1].length"));
    }

    [Fact]
    public void A_push_is_invisible_to_every_other_binding()
    {
        // The rule assigning to an element already follows: it rebuilds and rebinds rather
        // than mutating, so an array stays a value.
        Assert.Equal(2f, Radius("let a = [1, 2];\nlet b = a;\nb.push(9);", "a.length"));
    }

    [Fact]
    public void An_array_pushed_stays_one_element()
    {
        // Nothing flattens. Splicing is what a child position does, and this is not one.
        Assert.Equal(2f, Radius("let a = [1];\na.push([2, 3]);", "a[1].length"));
    }

    [Theory]
    [InlineData("let n = 3;\nn.push(1);", "cannot push onto a number")]
    [InlineData("let a = [1];\na.push(1, 2);", "'push' takes one value, found 2")]
    [InlineData("let a = [1];\nlet b = a.push(1);", "'push' is a statement")]
    [InlineData("PI.push(1);", "'PI' is a built-in")]
    [InlineData("a.push(1);", "unknown name 'a'")]
    public void Reports_a_push_it_cannot_make(string statement, string message)
    {
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) =
            TestSource.Load($"{statement}\nsphere {{ }}");

        Assert.Null(scene);
        Assert.Contains(diagnostics, d => d.Message.Contains(message));
    }
}
