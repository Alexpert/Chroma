using System.Numerics;
using ChromaTest.Core.Compilation;
using ChromaTest.Core.Sdl.Source;

namespace ChromaTest.Core.Tests;

/// <summary>
/// The scene-to-GPU stage. The matrix round-trip tests are the ones that matter: baking a
/// transform and inverting it is the only place a composition-order mistake can hide, and
/// it is invisible in the source — it shows up as a picture that looks almost right.
/// </summary>
public sealed class CompilationTests
{
    [Fact]
    public void Emits_one_leaf_per_primitive_in_order()
    {
        CompiledScene scene = TestSource.CompileValid("sphere { }\nbox { }\ncylinder { }");

        Assert.Equal(3, scene.InstructionCount);
        Assert.Equal(3, scene.PrimitiveCount);

        for (int i = 0; i < 3; i++)
        {
            Assert.Equal((int)TapeOpcode.Leaf, scene.Tape[(i * GpuLayout.TapeStride) + 0]);
            Assert.Equal(i, scene.Tape[(i * GpuLayout.TapeStride) + 1]);
        }

        Assert.Equal(
            [PrimitiveKind.Sphere, PrimitiveKind.Box, PrimitiveKind.Cylinder],
            Enumerable.Range(0, 3).Select(i => KindOf(scene, i)));
    }

    [Fact]
    public void Maps_a_sphere_onto_the_unit_sphere()
    {
        CompiledScene scene = TestSource.CompileValid("sphere { center: [1, 2, 3], radius: 2 }");
        Matrix4x4 toLocal = InverseOf(scene, 0);

        AssertClose(Vector3.Zero, ToLocal(toLocal, new Vector3(1f, 2f, 3f)));
        AssertClose(Vector3.UnitX, ToLocal(toLocal, new Vector3(3f, 2f, 3f)));
        AssertClose(Vector3.UnitY, ToLocal(toLocal, new Vector3(1f, 4f, 3f)));
    }

    [Fact]
    public void Maps_a_box_onto_the_unit_box()
    {
        CompiledScene scene = TestSource.CompileValid("box { min: [-2, -1, 0], max: [2, 1, 4] }");
        Matrix4x4 toLocal = InverseOf(scene, 0);

        AssertClose(Vector3.Zero, ToLocal(toLocal, new Vector3(0f, 0f, 2f)));
        AssertClose(new Vector3(1f, 1f, 1f), ToLocal(toLocal, new Vector3(2f, 1f, 4f)));
        AssertClose(new Vector3(-1f, -1f, -1f), ToLocal(toLocal, new Vector3(-2f, -1f, 0f)));
    }

    [Fact]
    public void Maps_a_cylinder_onto_the_canonical_cylinder()
    {
        CompiledScene scene = TestSource.CompileValid(
            "cylinder { base: [1, 0, 0], cap: [1, 3, 0], radius: 0.5 }");

        Matrix4x4 toLocal = InverseOf(scene, 0);

        AssertClose(Vector3.Zero, ToLocal(toLocal, new Vector3(1f, 0f, 0f)));
        AssertClose(Vector3.UnitY, ToLocal(toLocal, new Vector3(1f, 3f, 0f)));

        // The rotation about the cylinder's own axis is arbitrary, so a point on the rim
        // is pinned by its radius rather than by where it lands on the circle.
        Vector3 rim = ToLocal(toLocal, new Vector3(1.5f, 0f, 0f));
        Assert.Equal(0f, rim.Y, 4);
        Assert.Equal(1f, new Vector2(rim.X, rim.Z).Length(), 4);
    }

    [Fact]
    public void Maps_a_cylinder_that_is_not_vertical()
    {
        // A cylinder along +Y is the case the basis construction has to avoid taking as
        // its helper vector, so an off-axis one and an on-axis one both need checking.
        CompiledScene scene = TestSource.CompileValid(
            "cylinder { base: [0, 0, 0], cap: [4, 0, 0], radius: 1 }");

        Matrix4x4 toLocal = InverseOf(scene, 0);

        AssertClose(Vector3.Zero, ToLocal(toLocal, Vector3.Zero));
        AssertClose(Vector3.UnitY, ToLocal(toLocal, new Vector3(4f, 0f, 0f)));
    }

    [Fact]
    public void Composes_the_node_transform_after_the_shape()
    {
        // The shape maps local (1,0,0) to world (1,0,0); the scale then takes it to
        // (2,0,0) and the 90-degree turn about Y to (0,0,-2). Any other composition order
        // lands somewhere else.
        CompiledScene scene = TestSource.CompileValid(
            "sphere { radius: 1, scale: [2, 1, 1], rotate: [0, 90, 0] }");

        Matrix4x4 toLocal = InverseOf(scene, 0);

        AssertClose(Vector3.UnitX, ToLocal(toLocal, new Vector3(0f, 0f, -2f)));
        AssertClose(Vector3.Zero, ToLocal(toLocal, Vector3.Zero));
    }

    [Fact]
    public void Interns_a_shared_material_once()
    {
        CompiledScene scene = TestSource.CompileValid(
            """
            let red = material { color: [1, 0, 0] };
            sphere { material: red }
            box { material: red }
            cylinder { material: { color: [0, 1, 0] } }
            """);

        Assert.Equal(2, scene.MaterialCount);
        Assert.Equal(MaterialIndexOf(scene, 0), MaterialIndexOf(scene, 1));
        Assert.NotEqual(MaterialIndexOf(scene, 0), MaterialIndexOf(scene, 2));
    }

    [Fact]
    public void Falls_back_to_a_default_material()
    {
        CompiledScene scene = TestSource.CompileValid("sphere { }\nbox { }");

        Assert.Equal(1, scene.MaterialCount);
        Assert.Equal(0, MaterialIndexOf(scene, 0));
    }

    [Fact]
    public void Writes_the_material_table_in_the_documented_layout()
    {
        CompiledScene scene = TestSource.CompileValid(
            "sphere { material: { color: [0.25, 0.5, 0.75], specular: 0.4, shininess: 64 } }");

        Assert.Equal([0.25f, 0.5f, 0.75f, 0.4f], scene.Materials.Take(4));
        Assert.Equal(64f, scene.Materials[4]);
    }

    [Fact]
    public void Sums_the_span_budget_over_the_root_solids()
    {
        CompiledScene scene = TestSource.CompileValid("sphere { }\nbox { }\ncylinder { }");

        // Convex primitives are one span each, and nothing is nested, so one stack slot.
        Assert.Equal(3, scene.Budget.Spans);
        Assert.Equal(1, scene.Budget.StackDepth);
    }

    [Fact]
    public void Reports_a_transform_that_cannot_be_inverted()
    {
        // A zero scale collapses the solid to nothing. Silently emitting a singular matrix
        // would give the shader NaNs to chew on instead of an explanation.
        (CompiledScene? compiled, IReadOnlyList<Diagnostic> diagnostics) =
            TestSource.Compile("sphere { scale: [1, 0, 1] }");

        Assert.Null(compiled);
        Assert.Contains(diagnostics, d => d.Message.Contains("cannot be inverted"));
    }

    [Theory]
    [InlineData("union { sphere { } box { } }", "union")]
    [InlineData("intersection { sphere { } box { } }", "intersection")]
    [InlineData("difference { box { } sphere { } }", "difference")]
    public void Refuses_csg_operators_by_name(string body, string expected)
    {
        // Refusing beats drawing the operands' union: a picture the file does not explain
        // costs far more to diagnose than a message saying the feature is not there.
        (CompiledScene? compiled, IReadOnlyList<Diagnostic> diagnostics) = TestSource.Compile(body);

        Assert.Null(compiled);
        Assert.Contains(
            diagnostics,
            d => d.Message.Contains($"'{expected}' cannot be rendered yet"));
    }

    [Fact]
    public void Points_the_unsupported_operator_diagnostic_at_the_operator()
    {
        (_, IReadOnlyList<Diagnostic> diagnostics) =
            TestSource.Compile("\ndifference {\n  box { }\n  sphere { }\n}");

        Diagnostic error = Assert.Single(diagnostics);
        Assert.Equal((3, 1), error.Position);
    }

    private static PrimitiveKind KindOf(CompiledScene scene, int primitive) =>
        (PrimitiveKind)(int)scene.Primitives[primitive * GpuLayout.PrimitiveStride];

    private static int MaterialIndexOf(CompiledScene scene, int primitive) =>
        (int)scene.Primitives[(primitive * GpuLayout.PrimitiveStride) + 1];

    /// <summary>
    /// Reads back the world-to-local matrix, in the same row order the packer wrote it.
    /// </summary>
    private static Matrix4x4 InverseOf(CompiledScene scene, int primitive)
    {
        float[] p = scene.Primitives;
        int b = (primitive * GpuLayout.PrimitiveStride) + 4;

        return new Matrix4x4(
            p[b + 0], p[b + 1], p[b + 2], p[b + 3],
            p[b + 4], p[b + 5], p[b + 6], p[b + 7],
            p[b + 8], p[b + 9], p[b + 10], p[b + 11],
            p[b + 12], p[b + 13], p[b + 14], p[b + 15]);
    }

    private static Vector3 ToLocal(Matrix4x4 toLocal, Vector3 world) =>
        Vector3.Transform(world, toLocal);

    private static void AssertClose(Vector3 expected, Vector3 actual) =>
        Assert.True(Vector3.Distance(expected, actual) < 1e-4f, $"expected {expected}, got {actual}");
}
