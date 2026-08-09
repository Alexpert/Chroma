using ChromaTest.Core.Sdl.Syntax;

namespace ChromaTest.Core.Tests;

public sealed class ParserTests
{
    [Fact]
    public void Parses_a_let_declaration()
    {
        (SceneFile file, var diagnostics) = TestSource.Parse("let radius = 1.5;");

        Assert.Empty(diagnostics);
        LetStatement let = Assert.IsType<LetStatement>(Assert.Single(file.Statements));
        Assert.Equal("radius", let.Name);
        Assert.Equal(1.5, Assert.IsType<NumberExpression>(let.Value).Value);
    }

    [Fact]
    public void Distinguishes_a_node_from_a_reference()
    {
        // 'sphere { ... }' is a node; a bare 'radius' is a reference to a binding. One
        // token of lookahead separates them, and getting it wrong breaks both forms.
        (SceneFile file, var diagnostics) = TestSource.Parse("sphere { radius: radius }");

        Assert.Empty(diagnostics);
        ObjectExpression node = Assert.IsType<ObjectExpression>(
            Assert.IsType<ExpressionStatement>(Assert.Single(file.Statements)).Value);

        Assert.Equal("sphere", node.TypeName);
        FieldEntry field = Assert.IsType<FieldEntry>(Assert.Single(node.Entries));
        Assert.Equal("radius", field.Name);
        Assert.Equal("radius", Assert.IsType<IdentifierExpression>(field.Value).Name);
    }

    [Fact]
    public void Distinguishes_a_field_from_a_child()
    {
        (SceneFile file, var diagnostics) = TestSource.Parse(
            "difference { box { } sphere { } material: red }");

        Assert.Empty(diagnostics);
        ObjectExpression node = Assert.IsType<ObjectExpression>(
            Assert.IsType<ExpressionStatement>(Assert.Single(file.Statements)).Value);

        Assert.Collection(
            node.Entries,
            e => Assert.Equal("box", Assert.IsType<ObjectExpression>(Assert.IsType<ChildEntry>(e).Value).TypeName),
            e => Assert.Equal("sphere", Assert.IsType<ObjectExpression>(Assert.IsType<ChildEntry>(e).Value).TypeName),
            e => Assert.Equal("material", Assert.IsType<FieldEntry>(e).Name));
    }

    [Fact]
    public void Parses_an_anonymous_object_literal()
    {
        (SceneFile file, var diagnostics) = TestSource.Parse("sphere { material: { color: [1, 0, 0] } }");

        Assert.Empty(diagnostics);
        ObjectExpression node = Assert.IsType<ObjectExpression>(
            Assert.IsType<ExpressionStatement>(Assert.Single(file.Statements)).Value);

        FieldEntry field = Assert.IsType<FieldEntry>(Assert.Single(node.Entries));
        Assert.Null(Assert.IsType<ObjectExpression>(field.Value).TypeName);
    }

    [Fact]
    public void Treats_commas_as_optional()
    {
        (SceneFile withCommas, var a) = TestSource.Parse("sphere { center: [1, 2, 3], radius: 4 }");
        (SceneFile withoutCommas, var b) = TestSource.Parse("sphere { center: [1 2 3] radius: 4 }");

        Assert.Empty(a);
        Assert.Empty(b);

        ObjectExpression first = ExtractNode(withCommas);
        ObjectExpression second = ExtractNode(withoutCommas);

        Assert.Equal(first.Entries.Count, second.Entries.Count);
        Assert.Equal(
            first.Entries.OfType<FieldEntry>().Select(f => f.Name),
            second.Entries.OfType<FieldEntry>().Select(f => f.Name));
    }

    [Fact]
    public void Preserves_the_order_of_block_entries()
    {
        // A block is a list, not a dictionary: transform modifiers apply in written order,
        // so anything that reorders entries silently changes the geometry.
        (SceneFile file, _) = TestSource.Parse(
            "sphere { translate: [1, 0, 0] rotate: [0, 90, 0] translate: [0, 1, 0] }");

        Assert.Equal(
            ["translate", "rotate", "translate"],
            ExtractNode(file).Entries.OfType<FieldEntry>().Select(f => f.Name));
    }

    [Theory]
    [InlineData("1 + 2 * 3", 7.0)]
    [InlineData("(1 + 2) * 3", 9.0)]
    [InlineData("10 - 2 - 3", 5.0)]
    [InlineData("8 / 4 / 2", 1.0)]
    [InlineData("-2 * 3", -6.0)]
    public void Applies_operator_precedence_and_associativity(string expression, double expected)
    {
        (SceneFile file, var diagnostics) = TestSource.Parse($"let x = {expression};");

        Assert.Empty(diagnostics);
        LetStatement let = Assert.IsType<LetStatement>(Assert.Single(file.Statements));
        Assert.Equal(expected, Fold(let.Value), 10);
    }

    [Fact]
    public void Reports_an_unexpected_token_and_recovers()
    {
        // The point is the second statement: recovery has to leave the parser able to read
        // the rest of the file, not just able to stop politely.
        (SceneFile file, var diagnostics) = TestSource.Parse("sphere { radius: } box { }");

        Assert.NotEmpty(diagnostics);
        Assert.Equal(2, file.Statements.Count);
    }

    [Fact]
    public void Terminates_on_an_unclosed_block()
    {
        (SceneFile file, var diagnostics) = TestSource.Parse("sphere { radius: 1");

        Assert.NotEmpty(diagnostics);
        Assert.Single(file.Statements);
    }

    private static ObjectExpression ExtractNode(SceneFile file) =>
        Assert.IsType<ObjectExpression>(
            Assert.IsType<ExpressionStatement>(Assert.Single(file.Statements)).Value);

    private static double Fold(Expression expression) => expression switch
    {
        NumberExpression number => number.Value,
        UnaryExpression unary => -Fold(unary.Operand),
        BinaryExpression binary => binary.Operator switch
        {
            BinaryOperator.Add => Fold(binary.Left) + Fold(binary.Right),
            BinaryOperator.Subtract => Fold(binary.Left) - Fold(binary.Right),
            BinaryOperator.Multiply => Fold(binary.Left) * Fold(binary.Right),
            _ => Fold(binary.Left) / Fold(binary.Right),
        },
        _ => throw new InvalidOperationException($"unexpected {expression.GetType().Name}"),
    };
}
