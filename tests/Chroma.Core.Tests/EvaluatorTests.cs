using System.Numerics;
using Chroma.Core.Model;
using Chroma.Core.Model.Geometry.Primitives;
using Chroma.Core.Sdl.Source;

namespace Chroma.Core.Tests;

/// <summary>
/// Expression evaluation, observed through the value that reaches the scene model —
/// which is the only place it matters.
/// </summary>
public sealed class EvaluatorTests
{
    [Theory]
    [InlineData("1 + 2 * 3", 7f)]
    [InlineData("(1 + 2) * 3", 9f)]
    [InlineData("2 * 1.25", 2.5f)]
    [InlineData("-1.5", -1.5f)]
    public void Evaluates_scalar_arithmetic(string expression, float expected)
    {
        Scene scene = TestSource.LoadValid($"sphere {{ radius: {expression} }}");

        Assert.Equal(expected, Assert.IsType<Sphere>(scene.Roots[0]).Radius, 5);
    }

    [Fact]
    public void Multiplies_a_vector_by_a_scalar()
    {
        Scene scene = TestSource.LoadValid("sphere { center: [1, 2, 3] * 2 }");

        Assert.Equal(new Vector3(2f, 4f, 6f), Assert.IsType<Sphere>(scene.Roots[0]).Center);
    }

    [Fact]
    public void Adds_vectors_component_wise()
    {
        Scene scene = TestSource.LoadValid("sphere { center: [1, 2, 3] + [0, 1, 0] }");

        Assert.Equal(new Vector3(1f, 3f, 3f), Assert.IsType<Sphere>(scene.Roots[0]).Center);
    }

    [Fact]
    public void Broadcasts_a_scalar_across_a_vector()
    {
        Scene scene = TestSource.LoadValid("sphere { center: [1, 2, 3] - 1 }");

        Assert.Equal(new Vector3(0f, 1f, 2f), Assert.IsType<Sphere>(scene.Roots[0]).Center);
    }

    [Fact]
    public void Negates_a_vector()
    {
        Scene scene = TestSource.LoadValid("sphere { center: -[1, 2, 3] }");

        Assert.Equal(new Vector3(-1f, -2f, -3f), Assert.IsType<Sphere>(scene.Roots[0]).Center);
    }

    [Fact]
    public void Rejects_vectors_of_different_lengths()
    {
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) =
            TestSource.Load("sphere { center: [1, 2] + [1, 2, 3] }");

        Assert.Null(scene);
        Assert.Contains(diagnostics, d => d.Message.Contains("cannot combine"));
    }

    [Fact]
    public void Rejects_arithmetic_on_objects()
    {
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) =
            TestSource.Load("sphere { radius: material { } * 2 }");

        Assert.Null(scene);
        Assert.Contains(diagnostics, d => d.Message.Contains("objects do not support arithmetic"));
    }

    [Fact]
    public void Resolves_a_let_binding()
    {
        Scene scene = TestSource.LoadValid("let r = 2.5;\nsphere { radius: r }");

        Assert.Equal(2.5f, Assert.IsType<Sphere>(scene.Roots[0]).Radius);
    }

    [Fact]
    public void Resolves_a_let_binding_in_terms_of_an_earlier_one()
    {
        Scene scene = TestSource.LoadValid("let a = 2;\nlet b = a * 3;\nsphere { radius: b }");

        Assert.Equal(6f, Assert.IsType<Sphere>(scene.Roots[0]).Radius);
    }

    [Fact]
    public void Rejects_a_name_used_before_it_is_declared()
    {
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) =
            TestSource.Load("sphere { radius: r }\nlet r = 2;");

        Assert.Null(scene);
        Assert.Contains(diagnostics, d => d.Message.Contains("unknown name 'r'"));
    }

    [Fact]
    public void Rejects_a_redeclared_name()
    {
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) =
            TestSource.Load("let r = 1;\nlet r = 2;\nsphere { }");

        Assert.Null(scene);
        Assert.Contains(diagnostics, d => d.Message.Contains("'r' is already defined"));
    }

    [Fact]
    public void Rejects_a_non_numeric_vector_component()
    {
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) =
            TestSource.Load("sphere { center: [1, material { }, 3] }");

        Assert.Null(scene);
        Assert.Contains(diagnostics, d => d.Message.Contains("vector component must be a number"));
    }
}
