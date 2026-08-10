using System.Numerics;
using ChromaTest.Core.Compilation;
using ChromaTest.Core.Model;
using ChromaTest.Core.Model.Geometry;
using ChromaTest.Core.Model.Geometry.Primitives;
using ChromaTest.Core.Sdl.Source;

namespace ChromaTest.Core.Tests;

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
        Assert.Equal(expected, TestSource.CompileValid(body).Budget.Spans);
    }

    [Fact]
    public void Rejects_a_single_primitive_needing_more_spans_than_the_shader_holds()
    {
        // Nothing could overflow the budget on its own while every primitive was convex, so
        // the check lived on the operators. A lathe with enough segments reaches it alone,
        // and truncating instead would draw a solid with holes in it for no stated reason.
        int segments = GpuLayout.MaxSpans + 1;
        string points = string.Join(
            ", ",
            Enumerable.Range(0, segments).Select(i => $"1, {i}"));

        (CompiledScene? compiled, IReadOnlyList<Diagnostic> diagnostics) =
            TestSource.Compile($"lathe {{ points: [{points}] }}");

        Assert.Null(compiled);
        Assert.Contains(
            diagnostics,
            d => d.Message.Contains($"the shader holds {GpuLayout.MaxSpans}"));
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
