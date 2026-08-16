using Chroma.Core.Model;
using Chroma.Core.Model.Geometry.Primitives;
using Chroma.Core.Sdl.Source;

namespace Chroma.Core.Tests;

/// <summary>
/// <c>struct</c>: a record type declared in the scene file, in the C sense.
/// </summary>
/// <remarks>
/// The distinction being tested throughout is the one the declaration buys: a fixed set of
/// fields, checked where an instance is written, rather than an object literal that happens to
/// have the right keys.
/// </remarks>
public sealed class StructTests
{
    private const string Point = "struct Point { x, y }\n";

    private static float Radius(string body, string expression) =>
        Assert.IsType<Sphere>(
            TestSource.LoadValid($"{body}\nsphere {{ radius: {expression} }}").Roots[0]).Radius;

    private static IReadOnlyList<Diagnostic> Errors(string body)
    {
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) =
            TestSource.Load($"{body}\nsphere {{ }}");

        Assert.Null(scene);
        return diagnostics;
    }

    [Fact]
    public void Builds_an_instance_and_reads_a_field()
    {
        Assert.Equal(7f, Radius($"{Point}let p = Point {{ x: 3, y: 7 }};", "p.y"));
    }

    [Fact]
    public void Does_not_care_what_order_the_fields_are_written_in()
    {
        Assert.Equal(3f, Radius($"{Point}let p = Point {{ y: 7, x: 3 }};", "p.x"));
    }

    [Fact]
    public void Holds_values_of_any_kind()
    {
        Assert.Equal(
            2f,
            Radius(
                "struct Shape { solid, size, name }\n"
                + "let s = Shape { solid: sphere { }, size: [1, 2], name: \"bezier\" };",
                "s.size[1]"));
    }

    [Fact]
    public void Nests_inside_itself_and_inside_an_array()
    {
        Assert.Equal(
            5f,
            Radius(
                $"{Point}struct Edge {{ from, to }}\n"
                + "let edges = [Edge { from: Point { x: 0, y: 0 }, to: Point { x: 5, y: 1 } }];",
                "edges[0].to.x"));
    }

    [Fact]
    public void Is_passed_to_and_returned_from_a_function()
    {
        // The point of the entry: a helper had to take its parameters one number at a time and
        // could hand back exactly one solid.
        Assert.Equal(
            8f,
            Radius(
                $"{Point}function scaled(p, by) {{ return Point {{ x: p.x * by, y: p.y * by }}; }}",
                "scaled(Point { x: 2, y: 4 }, 2).y"));
    }

    [Fact]
    public void A_node_block_that_is_not_a_struct_type_is_still_a_node()
    {
        // Which of the two a block is comes from what its name resolves to, and a node name
        // resolves to nothing at all -- it is looked up by a binder, much later.
        Scene scene = TestSource.LoadValid($"{Point}sphere {{ radius: 4 }}");

        Assert.Equal(4f, Assert.IsType<Sphere>(scene.Roots[0]).Radius);
    }

    [Theory]

    // Same type, field by field, and nesting is followed.
    [InlineData("Point { x: 1, y: 2 } == Point { x: 1, y: 2 }", true)]
    [InlineData("Point { x: 1, y: 2 } == Point { x: 1, y: 3 }", false)]
    public void Compares_field_by_field(string expression, bool expected)
    {
        Assert.Equal(expected ? 1f : 2f, Radius(Point, $"({expression}) ? 1 : 2"));
    }

    [Fact]
    public void Refuses_to_compare_two_different_struct_types()
    {
        // Two types with the same field names are still two types, so this is the mistake
        // comparing a number with a string is, rather than a false.
        Assert.Contains(
            Errors(
                "struct A { x }\nstruct B { x }\n"
                + "let same = A { x: 1 } == B { x: 1 };"),
            d => d.Message.Contains("cannot compare a 'A' with a 'B'"));
    }

    [Fact]
    public void Reports_a_missing_field_at_the_instance()
    {
        Assert.Contains(
            Errors($"{Point}let p = Point {{ x: 1 }};"),
            d => d.Message == "'Point' is missing field 'y'");
    }

    [Fact]
    public void Lists_several_missing_fields_in_one_message()
    {
        // A struct written from memory tends to be missing more than one, and three messages
        // about one block is two too many.
        Assert.Contains(
            Errors("struct Post { at, height, tint }\nlet p = Post { at: 1 };"),
            d => d.Message == "'Post' is missing fields 'height', 'tint'");
    }

    [Fact]
    public void Reports_a_field_the_type_does_not_have()
    {
        Assert.Contains(
            Errors($"{Point}let p = Point {{ x: 1, y: 2, z: 3 }};"),
            d => d.Message == "'Point' has no field 'z'; it has 'x', 'y'");
    }

    [Fact]
    public void Reports_reading_a_field_the_type_does_not_have()
    {
        Assert.Contains(
            Errors($"{Point}let p = Point {{ x: 1, y: 2 }};\nlet z = p.z;"),
            d => d.Message == "'Point' has no field 'z'; it has 'x', 'y'");
    }

    [Fact]
    public void Reports_a_field_set_twice()
    {
        Assert.Contains(
            Errors($"{Point}let p = Point {{ x: 1, x: 2, y: 3 }};"),
            d => d.Message.Contains("field 'x' is set more than once on 'Point'"));
    }

    [Fact]
    public void Reports_a_child_object_inside_an_instance()
    {
        Assert.Contains(
            Errors($"{Point}let p = Point {{ x: 1, y: 2, sphere {{ }} }};"),
            d => d.Message.Contains("'Point' is a struct and takes only its fields"));
    }

    [Fact]
    public void Reports_a_repeated_field_in_the_declaration()
    {
        Assert.Contains(
            Errors("struct Point { x, x }"),
            d => d.Message.Contains("'x' is already a field of 'Point'"));
    }

    [Fact]
    public void Refuses_a_type_named_after_a_node()
    {
        // An instance is written with a node's syntax, so this would quietly turn every
        // 'sphere' block in the file into a record and leave the complaint to arrive several
        // stages later, about a value that cannot be a solid.
        Assert.Contains(
            Errors("struct sphere { radius }"),
            d => d.Message.Contains("'sphere' is the name of a node type"));
    }

    [Fact]
    public void Obeys_the_no_shadowing_rule_like_every_other_declaration()
    {
        Assert.Contains(
            Errors("let Point = 3;\nstruct Point { x }"),
            d => d.Message.Contains("'Point' is already defined"));
    }

    [Fact]
    public void Assigns_to_a_field()
    {
        Assert.Equal(5f, Radius($"{Point}let p = Point {{ x: 1, y: 2 }};\np.x = 5;", "p.x"));
    }

    [Fact]
    public void An_assignment_to_a_field_is_invisible_to_every_other_binding()
    {
        // The whole of the mutability decision, in one test. Assigning rebuilds and rebinds
        // rather than mutating, so a struct is still a value and 'let q = p;' copies nothing
        // and shares nothing -- which is the answer this language already gives for solids.
        Assert.Equal(
            1f,
            Radius($"{Point}let p = Point {{ x: 1, y: 2 }};\nlet q = p;\nq.x = 99;", "p.x"));
    }

    [Fact]
    public void An_assignment_inside_a_function_does_not_reach_the_caller()
    {
        Assert.Equal(
            1f,
            Radius(
                $"{Point}function bump(v) {{ v.x = 99; return v.x; }}\n"
                + "let p = Point { x: 1, y: 2 };\nlet ignored = bump(p);",
                "p.x"));
    }

    [Fact]
    public void Assigns_through_a_path_of_structs_and_arrays()
    {
        Assert.Equal(
            42f,
            Radius(
                $"{Point}struct Box {{ items }}\n"
                + "let b = Box { items: [Point { x: 0, y: 0 }] };\n"
                + "b.items[0].y = 42;",
                "b.items[0].y"));
    }

    [Fact]
    public void Reports_an_assignment_to_a_field_the_type_does_not_have()
    {
        Assert.Contains(
            Errors($"{Point}let p = Point {{ x: 1, y: 2 }};\np.z = 5;"),
            d => d.Message == "'Point' has no field 'z'; it has 'x', 'y'");
    }

    [Fact]
    public void Reports_an_assignment_whose_left_side_starts_with_no_name()
    {
        // There has to be a binding to write the rebuilt value back to, and a call's result
        // is not one.
        Assert.Contains(
            Errors($"{Point}function make() {{ return Point {{ x: 1, y: 2 }}; }}\nmake().x = 5;"),
            d => d.Message.Contains("has to start with a name"));
    }

    [Fact]
    public void Refuses_arithmetic_by_name()
    {
        Assert.Contains(
            Errors($"{Point}let n = Point {{ x: 1, y: 2 }} * 2;"),
            d => d.Message.Contains("'Point' structs do not support arithmetic"));
    }

    [Fact]
    public void Refuses_reading_a_field_of_a_node_block()
    {
        // The refusal is the distinction: a node is a description a binder later reads, and
        // letting it be probed by key would make every node's field set part of the language.
        Assert.Contains(
            Errors("let s = sphere { radius: 1 };\nlet r = s.radius;"),
            d => d.Message.Contains("is a node rather than a record"));
    }

    [Fact]
    public void Is_exported_by_an_import_like_any_other_declaration()
    {
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) = TestSource.LoadFiles(
            "scene.chroma",
            ("scene.chroma",
                TestSource.Camera
                + "import \"types.chroma\";\n"
                + "sphere { radius: Point { x: 6, y: 1 }.x }\n"),
            ("types.chroma", "struct Point { x, y }\n"));

        Assert.Empty(diagnostics);
        Assert.Equal(6f, Assert.IsType<Sphere>(scene!.Roots[0]).Radius);
    }
}
