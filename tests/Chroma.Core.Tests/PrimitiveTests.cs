using System.Numerics;
using Chroma.Core.Compilation;
using Chroma.Core.Model;
using Chroma.Core.Model.Geometry;
using Chroma.Core.Model.Geometry.Primitives;
using Chroma.Core.Sdl.Source;

namespace Chroma.Core.Tests;

/// <summary>
/// The six primitives added after the sphere, box and cylinder.
/// </summary>
/// <remarks>
/// Three things are worth asserting here and nothing else is reachable from the CPU. The
/// canonicalisation — which shape parameters survive as numbers and which are absorbed into
/// the matrix — is the contract the shader is written against. The span budget is what stops
/// a scene from silently rendering truncated geometry. And the shape buffer's layout is
/// agreed between the packer and the shader with nothing checking that it holds.
/// </remarks>
public sealed class PrimitiveTests
{
    [Fact]
    public void Emits_one_leaf_of_the_right_kind_for_each_new_primitive()
    {
        CompiledScene scene = TestSource.CompileValid(
            """
            cone  { base: [0, 0, 0], cap: [0, 1, 0], baseRadius: 1, capRadius: 0.2 }
            plane { normal: [0, 1, 0], distance: -1 }
            torus { majorRadius: 1, minorRadius: 0.3 }
            prism { points: [1, 0, 0, 1, -1, 0] }
            lathe { points: [0, 0, 1, 0, 0, 1] }
            blob  { blobSphere { radius: 1 } }
            """);

        Assert.Equal(
            [
                PrimitiveKind.Cone,
                PrimitiveKind.Plane,
                PrimitiveKind.Torus,
                PrimitiveKind.Prism,
                PrimitiveKind.Lathe,
                PrimitiveKind.Blob,
            ],
            Enumerable.Range(0, 6).Select(i => KindOf(scene, i)));
    }

    [Fact]
    public void Maps_a_cone_onto_the_canonical_cone_and_keeps_its_taper()
    {
        CompiledScene scene = TestSource.CompileValid(
            "cone { base: [0, 1, 0], cap: [0, 5, 0], baseRadius: 2, capRadius: 0.5 }");

        Matrix4x4 toLocal = InverseOf(scene, 0);

        AssertClose(Vector3.Zero, ToLocal(toLocal, new Vector3(0f, 1f, 0f)));
        AssertClose(Vector3.UnitY, ToLocal(toLocal, new Vector3(0f, 5f, 0f)));

        // The base is scaled to radius 1, and only the ratio of the two ends is left over.
        Assert.Equal(0.25f, ParamAOf(scene, 0), 5);
    }

    [Fact]
    public void Turns_a_cone_round_so_the_wider_end_is_the_base()
    {
        // The canonical cone tapers from 1 towards its cap, so a file that writes the narrow
        // end first describes the same solid seen the other way up. Swapping is what keeps
        // the taper in [0, 1] and the shader free of a second case.
        CompiledScene scene = TestSource.CompileValid(
            "cone { base: [0, 0, 0], cap: [0, 4, 0], baseRadius: 1, capRadius: 4 }");

        Matrix4x4 toLocal = InverseOf(scene, 0);

        AssertClose(Vector3.Zero, ToLocal(toLocal, new Vector3(0f, 4f, 0f)));
        AssertClose(Vector3.UnitY, ToLocal(toLocal, Vector3.Zero));
        Assert.Equal(0.25f, ParamAOf(scene, 0), 5);
    }

    [Fact]
    public void Maps_a_plane_onto_the_half_space_below_the_XZ_plane()
    {
        CompiledScene scene = TestSource.CompileValid(
            "plane { normal: [0, 2, 0], distance: 3 }");

        Matrix4x4 toLocal = InverseOf(scene, 0);

        // The normal is normalised on the way in, so 'distance' is measured in world units
        // however the normal was written.
        Assert.Equal(0f, ToLocal(toLocal, new Vector3(0f, 3f, 0f)).Y, 4);
        Assert.Equal(-1f, ToLocal(toLocal, new Vector3(0f, 2f, 0f)).Y, 4);
        Assert.Equal(1f, ToLocal(toLocal, new Vector3(0f, 4f, 0f)).Y, 4);
    }

    [Fact]
    public void Maps_a_tilted_plane_onto_the_same_half_space()
    {
        CompiledScene scene = TestSource.CompileValid(
            "plane { normal: [1, 0, 0], distance: 0 }");

        Matrix4x4 toLocal = InverseOf(scene, 0);

        // Local +Y is the plane's own normal, whichever way it points in the world.
        Assert.Equal(1f, ToLocal(toLocal, Vector3.UnitX).Y, 4);
        Assert.Equal(-1f, ToLocal(toLocal, -Vector3.UnitX).Y, 4);
    }

    [Fact]
    public void Maps_a_torus_onto_major_radius_one_and_keeps_the_minor_as_a_ratio()
    {
        CompiledScene scene = TestSource.CompileValid(
            "torus { center: [0, 2, 0], majorRadius: 4, minorRadius: 1 }");

        Matrix4x4 toLocal = InverseOf(scene, 0);

        AssertClose(Vector3.Zero, ToLocal(toLocal, new Vector3(0f, 2f, 0f)));
        AssertClose(Vector3.UnitX, ToLocal(toLocal, new Vector3(4f, 2f, 0f)));

        Assert.Equal(0.25f, ParamAOf(scene, 0), 5);
    }

    [Fact]
    public void Sweeps_a_prism_between_its_two_heights()
    {
        CompiledScene scene = TestSource.CompileValid(
            "prism { bottom: 1, top: 4, points: [1, 0, 0, 1, -1, 0] }");

        Matrix4x4 toLocal = InverseOf(scene, 0);

        // Only Y is canonicalised: the contour keeps the units the file wrote it in, which is
        // what lets one shape buffer serve a prism at any height.
        AssertClose(Vector3.Zero, ToLocal(toLocal, new Vector3(0f, 1f, 0f)));
        AssertClose(Vector3.UnitY, ToLocal(toLocal, new Vector3(0f, 4f, 0f)));
        AssertClose(new Vector3(2f, 0f, 3f), ToLocal(toLocal, new Vector3(2f, 1f, 3f)));
    }

    [Fact]
    public void Writes_a_contour_as_one_texel_per_closing_edge()
    {
        CompiledScene scene = TestSource.CompileValid(
            "prism { points: [1, 0, 0, 1, -1, 0] }");

        // Three points, three edges — the last one closing back to the first, which the file
        // never has to write.
        Assert.Equal(3 * GpuLayout.ShapeStride, scene.Shapes.Length);
        Assert.Equal([1f, 0f, 0f, 1f], scene.Shapes.Take(4));
        Assert.Equal([0f, 1f, -1f, 0f], scene.Shapes.Skip(4).Take(4));
        Assert.Equal([-1f, 0f, 1f, 0f], scene.Shapes.Skip(8).Take(4));
    }

    [Fact]
    public void Accepts_a_contour_that_repeats_its_first_point_to_close()
    {
        // POV-Ray's linear spline is closed by repeating the first point. Both spellings
        // describe the same contour, and the repeat would otherwise be a zero-length edge.
        CompiledScene repeated = TestSource.CompileValid(
            "prism { points: [1, 0, 0, 1, -1, 0, 1, 0] }");

        CompiledScene plain = TestSource.CompileValid(
            "prism { points: [1, 0, 0, 1, -1, 0] }");

        Assert.Equal(plain.Shapes, repeated.Shapes);
    }

    [Fact]
    public void Writes_a_blob_as_a_threshold_texel_and_two_per_component()
    {
        CompiledScene scene = TestSource.CompileValid(
            """
            blob {
              threshold: 0.6,
              blobSphere { center: [1, 2, 3], radius: 4, strength: 5 }
            }
            """);

        Assert.Equal(3 * GpuLayout.ShapeStride, scene.Shapes.Length);
        Assert.Equal([0.6f, 0f, 0f, 0f], scene.Shapes.Take(4));
        Assert.Equal([1f, 2f, 3f, 4f], scene.Shapes.Skip(4).Take(4));
        Assert.Equal(5f, scene.Shapes[8]);

        Assert.Equal(0f, ParamAOf(scene, 0));
        Assert.Equal(1f, ParamBOf(scene, 0));
    }

    [Fact]
    public void Points_each_primitive_at_its_own_stretch_of_the_shape_buffer()
    {
        CompiledScene scene = TestSource.CompileValid(
            """
            prism { points: [1, 0, 0, 1, -1, 0] }
            lathe { points: [0, 0, 1, 0, 1, 1, 0, 1] }
            """);

        Assert.Equal(0f, ParamAOf(scene, 0));
        Assert.Equal(3f, ParamBOf(scene, 0));

        Assert.Equal(3f, ParamAOf(scene, 1));
        Assert.Equal(4f, ParamBOf(scene, 1));
    }

    [Fact]
    public void Writes_a_sphere_sweep_as_one_texel_per_sphere()
    {
        CompiledScene scene = TestSource.CompileValid(
            "sphereSweep { spheres: [0, 0, 0, 1,  2, 0, 0, 0.5,  4, 0, 0, 0.25] }");

        Assert.Equal(PrimitiveKind.SphereSweep, KindOf(scene, 0));
        Assert.Equal(3 * GpuLayout.ShapeStride, scene.Shapes.Length);
        Assert.Equal([0f, 0f, 0f, 1f], scene.Shapes.Take(4));
        Assert.Equal([2f, 0f, 0f, 0.5f], scene.Shapes.Skip(4).Take(4));

        // The path is open, unlike a prism's or a lathe's contour, so three spheres are two
        // segments and the budget follows the segments rather than the spheres.
        Assert.Equal(3f, ParamBOf(scene, 0));
        Assert.Equal(2, scene.WidestRoot);
    }

    [Fact]
    public void Flattens_a_bezier_lathe_into_segments()
    {
        Scene scene = TestSource.LoadValid(
            """
            lathe {
              spline: "bezier",
              steps:  4,
              points: [
                0,   0,    0.5, 0,    0.8, 0.3,  0.8, 0.8,
                0.8, 0.8,  0.8, 1.3,  0.3, 1.6,  0,   2.0
              ]
            }
            """);

        Lathe lathe = Assert.IsType<Lathe>(Assert.Single(scene.Roots));

        // Two curves at four steps each. A curve contributes its intermediate points and its
        // end, never its start — that belongs to the curve before it, or to the closing edge.
        Assert.Equal(8, lathe.Points.Count);
        Assert.True(lathe.Smooth);

        // The first curve's end is the second's start, and it appears exactly once.
        Assert.Equal(new Vector2(0.8f, 0.8f), lathe.Points[3]);
    }

    [Fact]
    public void Marks_a_linear_lathe_as_faceted()
    {
        // Blending normals across joints is what makes a tessellated curve read as a curve,
        // and exactly what a hand-written outline must not get: its corners are deliberate.
        Scene scene = TestSource.LoadValid("lathe { points: [0, 0, 1, 0, 1, 1, 0, 1] }");

        Assert.False(Assert.IsType<Lathe>(Assert.Single(scene.Roots)).Smooth);
    }

    [Fact]
    public void Carries_the_smooth_flag_in_the_sign_of_the_segment_count()
    {
        // Both parameter slots are spoken for by the offset and the count, and a count is
        // never zero and never genuinely negative — so its sign is free storage.
        CompiledScene curved = TestSource.CompileValid(
            """
            lathe {
              spline: "bezier",
              steps:  4,
              points: [0, 0,  0.5, 0,  0.8, 0.3,  0.8, 0.8,
                       0.8, 0.8,  0.8, 1.3,  0.3, 1.6,  0, 2.0]
            }
            """);

        CompiledScene faceted = TestSource.CompileValid(
            "lathe { points: [0, 0, 1, 0, 1, 1, 0, 1] }");

        Assert.Equal(-8f, ParamBOf(curved, 0));
        Assert.Equal(4f, ParamBOf(faceted, 0));
    }

    [Fact]
    public void Holds_a_point_list_primitives_exact_span_count()
    {
        // Two Bézier curves at eight steps is sixteen segments, and sixteen is what the list
        // holds. It used to be clamped to eight, because every list in the shader was one
        // global size and raising that size stopped the shader linking — so a lathe past eight
        // spans along a ray rendered with a slice missing. A generated list is sized from its
        // own leaf, and there is nothing left to clamp against.
        CompiledScene scene = TestSource.CompileValid(
            """
            lathe {
              spline: "bezier",
              steps:  8,
              points: [0, 0,  0.5, 0,  0.8, 0.3,  0.8, 0.8,
                       0.8, 0.8,  0.8, 1.3,  0.3, 1.6,  0, 2.0]
            }
            """);

        Assert.Equal(16, Assert.IsType<Lathe>(Assert.Single(scene.Scene.Roots)).Points.Count);
        Assert.Equal(16, scene.WidestRoot);

        // And the crossing array is TWICE that, because every band can be entered and exited
        // by one ray. The interpreter's array held 32 and was checked against the SEGMENT count,
        // so a 24-segment lathe silently dropped crossings — which flips the parity of every
        // crossing after it and turns the rest of the solid inside out.
        //
        // The array is one global shared by every leaf, because a leaf owns its scratch for the
        // length of one call and no two are ever in flight at once. What is per-scene is its
        // size: the hungriest leaf, which here is the only one.
        Assert.Contains("float gCross[32];", scene.Geometry);
    }

    [Theory]
    [InlineData("cone { }", 1)]
    [InlineData("plane { }", 1)]
    [InlineData("torus { }", 2)]
    [InlineData("prism { points: [1, 0, 0, 1, -1, 0, 0, -1] }", 2)]
    [InlineData("lathe { points: [0, 0, 1, 0, 1, 1, 0, 1] }", 4)]
    [InlineData("blob { blobSphere { } blobSphere { center: [1, 0, 0] } }", 2)]
    public void Sizes_the_span_budget_from_the_primitive(string body, int expected)
    {
        // A convex leaf was worth one span and nothing else could be. A ray crosses a torus
        // twice, each wall of a prism once, and each band of a lathe on both sides of the
        // axis; a blob's field is a sum of single-humped bumps, so it has at most as many
        // stretches above the threshold as it has components.
        Assert.Equal(expected, TestSource.CompileValid(body).WidestRoot);
    }

    [Fact]
    public void Accepts_an_outline_far_past_the_old_shared_crossing_array()
    {
        // Forty segments needs eighty crossings. The interpreter held thirty-two for every
        // scene ever written, and refused this outline at the binder rather than truncate it
        // in the shader. Nothing here is shared any more.
        const int segments = 40;
        string points = string.Join(
            ", ",
            Enumerable.Range(0, segments).Select(i => $"1, {i}"));

        CompiledScene scene = TestSource.CompileValid($"lathe {{ points: [{points}] }}");

        Assert.Equal(segments, scene.WidestRoot);
        Assert.Contains($"float gCross[{2 * segments}];", scene.Geometry);
    }

    [Fact]
    public void Refuses_a_shape_past_the_size_worth_generating_code_for()
    {
        // These are no longer shared shader arrays that would truncate — every array is
        // generated at the size its own leaf needs. What they bound now is how much source one
        // solid emits, and how wide a span list it forces on every operator above it, so they
        // are generous sanity limits rather than walls. The diagnostic says "the limit is"
        // rather than "the shader holds" for exactly that reason.
        string tooMany = string.Join(
            ", ",
            Enumerable.Range(0, GpuLayout.MaxSweepSpheres + 1).Select(i => $"{i}, 0, 0, 1"));

        (CompiledScene? sweep, IReadOnlyList<Diagnostic> sweepErrors) =
            TestSource.Compile($"sphereSweep {{ spheres: [{tooMany}] }}");

        Assert.Null(sweep);
        Assert.Contains(
            sweepErrors,
            d => d.Message.Contains($"the limit is {GpuLayout.MaxSweepSpheres}"));

        // 9 curves at 8 steps is 72 points, past the 64 a contour may hold. Five curves — the
        // 40 points that used to be refused, because the shared crossing array held 32 — now
        // compile, which is the whole reason a chess bishop is expressible.
        string curves = string.Join(
            ", ",
            Enumerable.Repeat("0.5, 0, 0.8, 0.3, 0.8, 0.8, 0.5, 1", 9));

        (CompiledScene? lathe, IReadOnlyList<Diagnostic> latheErrors) =
            TestSource.Compile($"lathe {{ spline: \"bezier\", steps: 8, points: [{curves}] }}");

        Assert.Null(lathe);
        Assert.Contains(
            latheErrors,
            d => d.Message.Contains($"the limit is {GpuLayout.MaxContourPoints}")
                && d.Message.Contains("Lower 'steps'"));
    }

    [Fact]
    public void Leaves_the_shape_buffer_empty_for_a_scene_that_needs_none()
    {
        CompiledScene scene = TestSource.CompileValid("sphere { }\ntorus { }\ncone { }");

        Assert.Empty(scene.Shapes);
    }

    [Theory]
    [InlineData("cone { base: [0, 0, 0], cap: [0, 0, 0] }", "different points")]
    [InlineData("cone { baseRadius: 0, capRadius: 0 }", "above 0")]
    [InlineData("cone { baseRadius: -1 }", "not to be negative")]
    [InlineData("plane { normal: [0, 0, 0] }", "not the zero vector")]
    [InlineData("torus { majorRadius: 1, minorRadius: 1 }", "spindle torus is not supported")]
    [InlineData("torus { majorRadius: 0 }", "above 0")]
    [InlineData("prism { points: [1, 0, 0, 1, -1] }", "pairs of (x, z) values")]
    [InlineData("prism { points: [1, 0, 0, 1] }", "at least 3 points")]
    [InlineData("prism { points: [1, 0, 0, 1, -1, 0], bottom: 2, top: 2 }", "'bottom' and 'top' to differ")]
    [InlineData("prism { }", "requires a 'points' field")]
    [InlineData("lathe { points: [0, 0, -1, 0, 0, 1] }", "zero or above")]
    [InlineData("lathe { points: 3 }", "expects a vector")]
    [InlineData("blob { }", "at least one component")]
    [InlineData("blob { threshold: 0, blobSphere { } }", "'threshold' above 0")]
    [InlineData("blob { blobSphere { radius: 0 } }", "'radius' above 0")]
    [InlineData("blob { sphere { } }", "takes 'blobSphere' components")]
    [InlineData("sphereSweep { spheres: [0, 0, 0, 1] }", "at least 2 spheres")]
    [InlineData("sphereSweep { spheres: [0, 0, 0, 1, 2, 0, 0] }", "groups of (x, y, z, radius)")]
    [InlineData("sphereSweep { spheres: [0, 0, 0, 1, 2, 0, 0, 0] }", "radius in 'spheres' to be above 0")]
    [InlineData("sphereSweep { }", "requires a 'spheres' field")]
    [InlineData("lathe { spline: \"catmull\", points: [0, 0, 1, 0, 0, 1] }", "expects one of \"linear\", \"bezier\"")]
    [InlineData("lathe { spline: 2, points: [0, 0, 1, 0, 0, 1] }", "expects one of \"linear\", \"bezier\"")]
    [InlineData("lathe { spline: \"bezier\", points: [0, 0, 1, 0, 0, 1] }", "eight numbers per curve")]
    [InlineData("lathe { spline: \"bezier\", steps: 0, points: [0,0, 1,0, 1,1, 0,1] }", "between 1 and 64")]
    public void Reports_a_shape_it_cannot_draw(string body, string expected)
    {
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) = TestSource.Load(body);

        Assert.Null(scene);
        Assert.Contains(diagnostics, d => d.Message.Contains(expected));
    }

    [Fact]
    public void Binds_a_blob_component_as_an_anonymous_literal()
    {
        // The same shorthand a material already has: the field's type supplies the name.
        Scene scene = TestSource.LoadValid("blob { { center: [1, 0, 0], strength: 2 } }");

        Blob blob = Assert.IsType<Blob>(Assert.Single(scene.Roots));
        Assert.Equal(new BlobSphere(Vector3.UnitX, 1f, 2f), Assert.Single(blob.Components));
    }

    [Fact]
    public void Orders_a_prism_bottom_before_top()
    {
        // Written the other way round the sweep would have a negative height, and the
        // canonical form would be inside out rather than absent — a solid whose normals all
        // point the wrong way, with nothing in the file to suggest why.
        Scene scene = TestSource.LoadValid(
            "prism { bottom: 4, top: 1, points: [1, 0, 0, 1, -1, 0] }");

        Prism prism = Assert.IsType<Prism>(Assert.Single(scene.Roots));
        Assert.Equal(1f, prism.Bottom);
        Assert.Equal(4f, prism.Top);
    }

    private static PrimitiveKind KindOf(CompiledScene scene, int primitive) =>
        (PrimitiveKind)(int)scene.Primitives[primitive * GpuLayout.PrimitiveStride];

    private static float ParamAOf(CompiledScene scene, int primitive) =>
        scene.Primitives[(primitive * GpuLayout.PrimitiveStride) + 2];

    private static float ParamBOf(CompiledScene scene, int primitive) =>
        scene.Primitives[(primitive * GpuLayout.PrimitiveStride) + 3];

    /// <summary>
    /// Reads back the world-to-local matrix, in the same row order the packer wrote it.
    /// </summary>
    private static Matrix4x4 InverseOf(CompiledScene scene, int primitive)
    {
        int at = (primitive * GpuLayout.PrimitiveStride) + 4;

        return new Matrix4x4(
            scene.Primitives[at + 0], scene.Primitives[at + 1], scene.Primitives[at + 2], scene.Primitives[at + 3],
            scene.Primitives[at + 4], scene.Primitives[at + 5], scene.Primitives[at + 6], scene.Primitives[at + 7],
            scene.Primitives[at + 8], scene.Primitives[at + 9], scene.Primitives[at + 10], scene.Primitives[at + 11],
            scene.Primitives[at + 12], scene.Primitives[at + 13], scene.Primitives[at + 14], scene.Primitives[at + 15]);
    }

    private static Vector3 ToLocal(Matrix4x4 toLocal, Vector3 world) =>
        Vector3.Transform(world, toLocal);

    private static void AssertClose(Vector3 expected, Vector3 actual)
    {
        Assert.Equal(expected.X, actual.X, 4);
        Assert.Equal(expected.Y, actual.Y, 4);
        Assert.Equal(expected.Z, actual.Z, 4);
    }
}
