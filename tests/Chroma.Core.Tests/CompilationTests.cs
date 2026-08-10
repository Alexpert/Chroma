using System.Numerics;
using Chroma.Core.Compilation;
using Chroma.Core.Sdl.Source;

namespace Chroma.Core.Tests;

/// <summary>
/// The scene-to-GPU stage. The matrix round-trip tests are the ones that matter: baking a
/// transform and inverting it is the only place a composition-order mistake can hide, and
/// it is invisible in the source — it shows up as a picture that looks almost right.
/// </summary>
public sealed class CompilationTests
{
    [Fact]
    public void Emits_one_leaf_per_primitive_and_closes_every_root()
    {
        CompiledScene scene = TestSource.CompileValid("sphere { }\nbox { }\ncylinder { }");

        Assert.Equal(3, scene.PrimitiveCount);
        Assert.Equal(
            [
                (TapeOpcode.Leaf, 0), (TapeOpcode.EndRoot, 0),
                (TapeOpcode.Leaf, 1), (TapeOpcode.EndRoot, 0),
                (TapeOpcode.Leaf, 2), (TapeOpcode.EndRoot, 0),
            ],
            TapeOf(scene));

        Assert.Equal(
            [PrimitiveKind.Sphere, PrimitiveKind.Box, PrimitiveKind.Cylinder],
            Enumerable.Range(0, 3).Select(i => KindOf(scene, i)));
    }

    [Fact]
    public void Flattens_an_operator_into_post_order()
    {
        CompiledScene scene = TestSource.CompileValid("difference { box { } sphere { } }");

        Assert.Equal(
            [
                (TapeOpcode.Leaf, 0),
                (TapeOpcode.Leaf, 1),
                (TapeOpcode.Difference, 0),
                (TapeOpcode.EndRoot, 0),
            ],
            TapeOf(scene));
    }

    [Fact]
    public void Binarises_an_n_ary_operator_into_a_left_leaning_chain()
    {
        CompiledScene scene = TestSource.CompileValid(
            "union { sphere { } box { } cylinder { } }");

        Assert.Equal(
            [
                (TapeOpcode.Leaf, 0),
                (TapeOpcode.Leaf, 1),
                (TapeOpcode.Union, 0),
                (TapeOpcode.Leaf, 2),
                (TapeOpcode.Union, 0),
                (TapeOpcode.EndRoot, 0),
            ],
            TapeOf(scene));

        // Left association is the reason a long chain is cheap: every step merges the
        // accumulated list with one fresh operand, so the depth never grows past 2.
        Assert.Equal(2, scene.Budget.StackDepth);
    }

    [Fact]
    public void Nests_operators_in_the_order_they_are_written()
    {
        CompiledScene scene = TestSource.CompileValid(
            """
            difference {
              intersection { box { } sphere { } }
              cylinder { }
            }
            """);

        Assert.Equal(
            [
                (TapeOpcode.Leaf, 0),
                (TapeOpcode.Leaf, 1),
                (TapeOpcode.Intersection, 0),
                (TapeOpcode.Leaf, 2),
                (TapeOpcode.Difference, 0),
                (TapeOpcode.EndRoot, 0),
            ],
            TapeOf(scene));
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
        // Four texels: colour+roughness, emission+metallic, absorption+transmission, ior.
        // Every scalar rides in the alpha slot of a colour texel, so a swap here is
        // invisible until the picture is wrong.
        CompiledScene scene = TestSource.CompileValid(
            """
            sphere {
              material: {
                color:        [0.25, 0.5, 0.75],
                roughness:    0.4,
                emission:     [2, 3, 4],
                absorption:   [0.1, 0.2, 0.3],
                transmission: 0.6,
                ior:          1.7
              }
            }
            """);

        Assert.Equal([0.25f, 0.5f, 0.75f, 0.4f], scene.Materials.Take(4));
        Assert.Equal([2f, 3f, 4f, 0f], scene.Materials.Skip(4).Take(4));
        Assert.Equal([0.1f, 0.2f, 0.3f, 0.6f], scene.Materials.Skip(8).Take(4));
        Assert.Equal([1.7f, 0f, 0f, 0f], scene.Materials.Skip(12).Take(4));
    }

    [Fact]
    public void Zeroes_the_transmission_of_a_metal_in_the_table()
    {
        // A metal has no transmission lobe, so the shader would ignore the field anyway.
        // Zeroing it at compile time is what lets HasTransmission below be a plain look at
        // the table rather than a second walk over the model.
        CompiledScene scene = TestSource.CompileValid(
            "sphere { material: { metallic: 1, transmission: 1 } }");

        Assert.Equal(0f, scene.Materials[2 * 4 + 3]);
        Assert.False(scene.HasTransmission);
    }

    [Fact]
    public void Reports_whether_any_material_transmits()
    {
        // This is what keeps an opaque scene on the cheap shadow ray it has always used.
        CompiledScene opaque = TestSource.CompileValid("sphere { }\nbox { }");
        CompiledScene glass = TestSource.CompileValid(
            "sphere { }\nbox { material: { transmission: 0.5 } }");

        Assert.False(opaque.HasTransmission);
        Assert.True(glass.HasTransmission);
    }

    [Fact]
    public void Keeps_materials_distinct_when_only_a_transmissive_field_differs()
    {
        // Interning is by value, and the record's generated equality has to cover the new
        // fields or two different glasses would collapse into one.
        CompiledScene scene = TestSource.CompileValid(
            """
            sphere { material: { transmission: 1, ior: 1.5 } }
            box    { material: { transmission: 1, ior: 1.8 } }
            cylinder { material: { transmission: 1, ior: 1.5, absorption: [1, 0, 0] } }
            """);

        Assert.Equal(3, scene.MaterialCount);
    }

    [Fact]
    public void Leaves_emission_unclamped()
    {
        // Emission is a radiance, not a colour: a light is not limited to 1.
        CompiledScene scene = TestSource.CompileValid(
            "sphere { material: { emission: [40, 0, 0] } }");

        Assert.Equal(40f, scene.Materials[4]);
    }

    [Fact]
    public void Takes_the_span_budget_as_a_max_over_roots_rather_than_a_sum()
    {
        CompiledScene scene = TestSource.CompileValid("sphere { }\nbox { }\ncylinder { }");

        // The shader resolves one root at a time and reuses the same arrays, so twenty
        // separate spheres cost exactly what one costs. Summing here would make a scene of
        // scattered solids overflow a budget that renders a far harder CSG tree.
        Assert.Equal(1, scene.Budget.Spans);
        Assert.Equal(1, scene.Budget.StackDepth);
    }

    [Theory]
    [InlineData("union { sphere { } box { } cylinder { } }", 3)]
    [InlineData("intersection { sphere { } box { } cylinder { } }", 1)]
    [InlineData("difference { sphere { } box { } cylinder { } }", 3)]
    public void Sizes_the_span_budget_from_the_operator(string body, int expected)
    {
        // Union interleaves, so the counts add; intersection cannot exceed its thinner
        // operand; difference is A n complement(B), which emits at most |A| + |B|.
        Assert.Equal(expected, TestSource.CompileValid(body).Budget.Spans);
    }

    [Fact]
    public void Counts_stack_depth_against_nesting_on_the_right()
    {
        // The right operand is evaluated while the left result is still on the stack, so
        // depth comes from nesting on the right — not from the tree's height.
        CompiledScene scene = TestSource.CompileValid(
            "difference { box { } union { sphere { } cylinder { } } }");

        Assert.Equal(3, scene.Budget.StackDepth);

        CompiledScene mirrored = TestSource.CompileValid(
            "difference { union { sphere { } cylinder { } } box { } }");

        Assert.Equal(2, mirrored.Budget.StackDepth);
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

    [Fact]
    public void Composes_a_parent_transform_into_every_child()
    {
        // Only operators have children, so an inherited transform is reachable for the
        // first time in this iteration.
        CompiledScene scene = TestSource.CompileValid(
            """
            union {
              sphere { radius: 2 }
              box { }

              translate: [4, 0, 0]
            }
            """);

        Matrix4x4 sphere = InverseOf(scene, 0);
        AssertClose(Vector3.Zero, ToLocal(sphere, new Vector3(4f, 0f, 0f)));
        AssertClose(Vector3.UnitX, ToLocal(sphere, new Vector3(6f, 0f, 0f)));

        AssertClose(Vector3.One, ToLocal(InverseOf(scene, 1), new Vector3(5f, 1f, 1f)));
    }

    [Fact]
    public void Composes_transforms_through_several_levels()
    {
        CompiledScene scene = TestSource.CompileValid(
            """
            union {
              union {
                sphere { }
                translate: [1, 0, 0]
              }
              sphere { }

              translate: [0, 2, 0]
            }
            """);

        AssertClose(Vector3.Zero, ToLocal(InverseOf(scene, 0), new Vector3(1f, 2f, 0f)));
        AssertClose(Vector3.Zero, ToLocal(InverseOf(scene, 1), new Vector3(0f, 2f, 0f)));
    }

    [Fact]
    public void Inherits_a_parent_material_and_lets_a_child_override_it()
    {
        CompiledScene scene = TestSource.CompileValid(
            """
            let red  = material { color: [1, 0, 0] };
            let blue = material { color: [0, 0, 1] };

            union {
              sphere { }
              box { material: blue }

              material: red
            }
            """);

        Assert.Equal(2, scene.MaterialCount);

        int inherited = MaterialIndexOf(scene, 0);
        Assert.NotEqual(inherited, MaterialIndexOf(scene, 1));
        Assert.Equal(
            [1f, 0f, 0f],
            scene.Materials.Skip(inherited * GpuLayout.MaterialStride).Take(3));
    }

    [Fact]
    public void Rejects_a_subtree_needing_more_spans_than_the_shader_holds()
    {
        // Truncating instead would draw geometry that is subtly wrong in a way
        // indistinguishable from a bug in the merge itself.
        string operands = string.Join("\n  ", Enumerable.Repeat("sphere { }", GpuLayout.MaxSpans + 1));

        (CompiledScene? compiled, IReadOnlyList<Diagnostic> diagnostics) =
            TestSource.Compile($"\nunion {{\n  {operands}\n}}");

        Assert.Null(compiled);

        // One diagnostic, on the operator, not one per enclosing level.
        Diagnostic error = Assert.Single(diagnostics);
        Assert.Equal((3, 1), error.Position);
        Assert.Contains($"the shader holds {GpuLayout.MaxSpans}", error.Message);
    }

    [Fact]
    public void Reports_the_innermost_offending_subtree_only()
    {
        // Every enclosing operator overflows as well; a diagnostic for each would bury the
        // one line worth reading.
        string operands = string.Join("\n    ", Enumerable.Repeat("sphere { }", GpuLayout.MaxSpans + 1));

        (_, IReadOnlyList<Diagnostic> diagnostics) =
            TestSource.Compile($"union {{\n  union {{\n    {operands}\n  }}\n  box {{ }}\n}}");

        Assert.Single(diagnostics);
    }

    /// <summary>The tape as (opcode, operand) pairs, which is how it reads on paper.</summary>
    private static (TapeOpcode Opcode, int Operand)[] TapeOf(CompiledScene scene) =>
    [
        .. Enumerable.Range(0, scene.InstructionCount).Select(i => (
            (TapeOpcode)scene.Tape[i * GpuLayout.TapeStride],
            scene.Tape[(i * GpuLayout.TapeStride) + 1])),
    ];

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
