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
    public void Emits_one_function_per_primitive_and_one_per_root()
    {
        CompiledScene scene = TestSource.CompileValid("sphere { }\nbox { }\ncylinder { }");

        Assert.Equal(3, scene.PrimitiveCount);
        Assert.Equal(["leaf0", "leaf1", "leaf2"], CallsOf(scene));

        // Three separate roots, each resolved on its own: a scene of separate solids costs one
        // span list per solid rather than one holding all of them.
        Assert.Equal(3, Matches(scene, @"void shape\d+\("));
        Assert.Equal(1, scene.WidestRoot);

        Assert.Equal(
            [PrimitiveKind.Sphere, PrimitiveKind.Box, PrimitiveKind.Cylinder],
            Enumerable.Range(0, 3).Select(i => KindOf(scene, i)));
    }

    [Fact]
    public void Emits_operands_before_the_operator_that_consumes_them()
    {
        CompiledScene scene = TestSource.CompileValid("difference { box { } sphere { } }");

        Assert.Equal(["leaf0", "leaf1", "csgDifference_1_1_2"], CallsOf(scene));
    }

    [Fact]
    public void Binarises_an_n_ary_operator_into_a_left_leaning_chain()
    {
        CompiledScene scene = TestSource.CompileValid(
            "union { sphere { } box { } cylinder { } }");

        // Left association is the reason a long chain is cheap: every step merges the
        // accumulated list with one fresh operand, so only two lists are ever live at once.
        Assert.Equal(
            ["leaf0", "leaf1", "csgUnion_1_1_2", "leaf2", "csgUnion_2_1_3"],
            CallsOf(scene));
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
            ["leaf0", "leaf1", "csgIntersection_1_1_1", "leaf2", "csgDifference_1_1_2"],
            CallsOf(scene));
    }

    [Fact]
    public void Sizes_each_span_list_from_its_own_node()
    {
        // The whole point of generating the tree: a leaf holds one span, not the widest list
        // any scene might need. Under the interpreter every one of these was eight.
        CompiledScene scene = TestSource.CompileValid(
            "union { sphere { } union { box { } cylinder { } } }");

        Assert.Contains("struct SpanList_1 { int count; Span items[1]; };", scene.Geometry);
        Assert.Contains("struct SpanList_3 { int count; Span items[3]; };", scene.Geometry);
        Assert.DoesNotContain("SpanList_8", scene.Geometry);
        Assert.Equal(3, scene.WidestRoot);
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
        // Four texels: colour+roughness, emission+metallic, absorption+transmission, and
        // ior+medium. Every scalar rides in a slot beside a colour rather than taking a texel
        // of its own, so a swap here is invisible until the picture is wrong.
        CompiledScene scene = TestSource.CompileValid(
            """
            sphere {
              material: {
                color:        [0.25, 0.5, 0.75],
                roughness:    0.4,
                emission:     [2, 3, 4],
                absorption:   [0.1, 0.2, 0.3],
                transmission: 0.6,
                ior:          1.7,
                scattering:   0.8,
                anisotropy:   -0.3
              }
            }
            """);

        Assert.Equal([0.25f, 0.5f, 0.75f, 0.4f], scene.Materials.Take(4));
        Assert.Equal([2f, 3f, 4f, 0f], scene.Materials.Skip(4).Take(4));
        Assert.Equal([0.1f, 0.2f, 0.3f, 0.6f], scene.Materials.Skip(8).Take(4));
        Assert.Equal([1.7f, 0.8f, -0.3f, 0f], scene.Materials.Skip(12).Take(4));
    }

    [Fact]
    public void Fits_the_medium_into_the_slots_iteration_5_left_spare()
    {
        // The record did not grow. Iteration 5 wrote `ior` into a texel of its own and left
        // three floats beside it unused; a medium needed two of them. If this ever changes,
        // MATERIAL_TEXELS in raytrace.frag has to change with it, and nothing checks that.
        Assert.Equal(4 * 4, GpuLayout.MaterialStride);
    }

    [Fact]
    public void Zeroes_the_scattering_of_an_opaque_material_in_the_table()
    {
        // A medium lives inside a solid that light can enter. Zeroing it at compile time is
        // what lets HasMedia be a plain look at the table, and it matches what the binder
        // already warns about.
        CompiledScene scene = TestSource.CompileValid(
            "sphere { material: { scattering: 0.9 } }");

        Assert.Equal(0f, scene.Materials[3 * 4 + 1]);
        Assert.False(scene.HasMedia);
    }

    [Fact]
    public void Reports_whether_any_material_scatters()
    {
        // This is what keeps a scene with no medium on iteration 5's straight walk from
        // surface to surface, paying nothing for machinery it does not use.
        CompiledScene glass = TestSource.CompileValid(
            "sphere { material: { transmission: 1, absorption: [1, 1, 1] } }");
        CompiledScene fog = TestSource.CompileValid(
            "sphere { material: { transmission: 1, scattering: 0.2 } }");

        Assert.False(glass.HasMedia);
        Assert.True(fog.HasMedia);
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
    public void Sizes_each_root_on_its_own_rather_than_against_the_scene()
    {
        CompiledScene scene = TestSource.CompileValid("sphere { }\nbox { }\ncylinder { }");

        // Each root is resolved on its own, so twenty separate spheres cost exactly what one
        // costs. A scene of scattered solids is not a hard scene.
        Assert.Equal(1, scene.WidestRoot);
    }

    [Theory]
    [InlineData("union { sphere { } box { } cylinder { } }", 3)]
    [InlineData("intersection { sphere { } box { } cylinder { } }", 1)]
    [InlineData("difference { sphere { } box { } cylinder { } }", 3)]
    public void Sizes_a_span_list_from_the_operator_that_fills_it(string body, int expected)
    {
        // Union interleaves, so the counts add. Intersection is |A| + |B| - 1: the sweep
        // advances one pointer per emitted span, so a single long span meeting three short
        // ones produces three — min(|A|, |B|) was never a bound, and it went unnoticed only
        // because every list was eight spans wide whatever the scene said. Difference is
        // A n complement(B), which emits at most |A| + |B|.
        Assert.Equal(expected, TestSource.CompileValid(body).WidestRoot);
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
    public void Accepts_a_subtree_wider_than_the_old_shared_span_limit()
    {
        // Nine spheres in one union needed nine spans, and the interpreter's shared list held
        // eight — so this was an error, and raising the constant to ten stopped the shader
        // linking at all. Generated lists are sized per node, so the scene is simply compiled.
        CompiledScene scene = TestSource.CompileValid(
            "union {\n  " + string.Join("\n  ", Enumerable.Repeat("sphere { }", 9)) + "\n}");

        Assert.Equal(9, scene.WidestRoot);
        Assert.Contains("struct SpanList_9 { int count; Span items[9]; };", scene.Geometry);
    }

    /// <summary>
    /// The calls inside the generated root functions, in the order the shader runs them: one
    /// per leaf evaluated and one per operator applied.
    /// </summary>
    /// <remarks>
    /// This is what the post-order tape used to be asserted against. The tree is the same
    /// tree; only its representation changed, from an array of opcodes to nested calls over
    /// named locals.
    /// </remarks>
    private static string[] CallsOf(CompiledScene scene)
    {
        string roots = RootSection(scene);

        return
        [
            .. System.Text.RegularExpressions.Regex
                .Matches(roots, @"(leaf\d+|csg[A-Za-z]+_[\d_]+)\(")
                .Select(match => match.Groups[1].Value),
        ];
    }

    private static int Matches(CompiledScene scene, string pattern) =>
        System.Text.RegularExpressions.Regex.Matches(scene.Geometry, pattern).Count;

    /// <summary>Just the root functions, so operator bodies do not count as calls.</summary>
    private static string RootSection(CompiledScene scene)
    {
        int from = scene.Geometry.IndexOf("// --- Roots", StringComparison.Ordinal);
        int to = scene.Geometry.IndexOf("// --- The scene", StringComparison.Ordinal);

        Assert.InRange(from, 0, to);
        return scene.Geometry[from..to];
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
