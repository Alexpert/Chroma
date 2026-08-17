using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Chroma.Core;
using Chroma.Core.Compilation;
using Chroma.Core.Sdl.Source;

namespace Chroma.Core.Tests;

/// <summary>
/// The <c>mesh</c> primitive: what the decoders accept, what the topology check refuses, and
/// what the emitter uploads.
/// </summary>
/// <remarks>
/// <para>
/// The refusals carry most of the weight here, because they are the part of this primitive that
/// is genuinely new. Every other solid is a solid by construction; a mesh is one only if the
/// file says so, and "is this mesh closed" is a question that has to be asked and answered
/// before anything downstream may assume the parity of a ray's crossings means anything.
/// </para>
/// <para>
/// The cube below is the smallest closed, manifold, consistently oriented mesh worth writing,
/// and every failing case in this file is that cube broken in one specific way.
/// </para>
/// </remarks>
public sealed class MeshTests
{
    /// <summary>The eight corners of the unit cube, in the order the faces below index them.</summary>
    private static readonly (float X, float Y, float Z)[] CubeVertices =
    [
        (0, 0, 0), (1, 0, 0), (1, 1, 0), (0, 1, 0),
        (0, 0, 1), (1, 0, 1), (1, 1, 1), (0, 1, 1),
    ];

    /// <summary>
    /// The six faces as quads, each wound counter-clockwise seen from outside, one-based as an
    /// OBJ writes them.
    /// </summary>
    private static readonly int[][] CubeFaces =
    [
        [5, 6, 7, 8],   // z = 1
        [1, 4, 3, 2],   // z = 0
        [1, 5, 8, 4],   // x = 0
        [2, 3, 7, 6],   // x = 1
        [1, 2, 6, 5],   // y = 0
        [4, 8, 7, 3],   // y = 1
    ];

    [Fact]
    public void Loads_a_closed_obj_cube_as_one_leaf()
    {
        CompiledScene scene = CompiledWith("mesh { file: \"cube.obj\" }", ("cube.obj", CubeObj()));

        Assert.Equal(PrimitiveKind.Mesh, KindOf(scene, 0));

        // Two triangles per quad, and every one of them reached the buffer.
        Assert.Equal(12f, ParamBOf(scene, 0));
    }

    [Fact]
    public void Reads_every_form_an_obj_face_vertex_can_take()
    {
        // v, v/vt, v//vn and v/vt/vn describe the same corner, and a negative index counts back
        // from what has been declared so far. A file mixing them is unusual and legal, and each
        // form is the one some exporter writes.
        string obj = """
            v 0 0 0
            v 1 0 0
            v 1 1 0
            v 0 1 0
            v 0 0 1
            v 1 0 1
            v 1 1 1
            v 0 1 1
            vt 0 0
            vn 0 0 1
            f 5/1 6/1 7/1 8/1
            f 1//1 4//1 3//1 2//1
            f 1/1/1 5/1/1 8/1/1 4/1/1
            f 2 3 7 6
            f 1 2 6 5
            f -5 -1 -2 -6
            """;

        CompiledScene scene = CompiledWith("mesh { file: \"cube.obj\" }", ("cube.obj", obj));

        Assert.Equal(12f, ParamBOf(scene, 0));
    }

    [Fact]
    public void Welds_a_binary_stl_back_into_shared_vertices()
    {
        // STL writes every corner of every facet in full, so the cube arrives as 36 positions
        // sharing nothing. Without welding every edge is a boundary edge and every STL in
        // existence is refused as an open surface, so this is the test that the loader is usable
        // at all rather than a detail of it.
        CompiledScene scene = CompiledWith("mesh { file: \"cube.stl\" }", ("cube.stl", BinaryStl()));

        Assert.Equal(8, VertexCountOf(scene, 0));
        Assert.Equal(12f, ParamBOf(scene, 0));
    }

    [Fact]
    public void Reads_an_ascii_stl_as_well_as_a_binary_one()
    {
        CompiledScene scene = CompiledWith("mesh { file: \"cube.stl\" }", ("cube.stl", AsciiStl()));

        Assert.Equal(8, VertexCountOf(scene, 0));
        Assert.Equal(12f, ParamBOf(scene, 0));
    }

    [Fact]
    public void Refuses_a_mesh_with_a_hole_and_counts_its_boundary()
    {
        // The top face removed. Four edges are then traversed once and never come back, which is
        // exactly the count the diagnostic has to be able to state: "not closed" on its own is
        // not something anyone can act on.
        string message = OneError("mesh { file: \"open.obj\" }", ("open.obj", CubeObj(skipFace: 5)));

        Assert.Contains("not closed", message);
        Assert.Contains("4 boundary edges", message);
        Assert.Contains("close: true", message);
    }

    [Fact]
    public void Closes_a_hole_when_the_scene_asks_for_it()
    {
        // The same file the test above refuses. Four boundary edges become one fan of four
        // triangles round a new centre, so sixteen triangles rather than the twelve a whole cube
        // has: ten from the five surviving faces, plus the four of the cap, plus nothing else.
        CompiledScene scene = CompiledWith(
            "mesh { file: \"open.obj\", close: true }",
            ("open.obj", CubeObj(skipFace: 5)));

        Assert.Equal(14f, ParamBOf(scene, 0));
        Assert.Equal(9, VertexCountOf(scene, 0));
    }

    [Fact]
    public void Refuses_inconsistent_winding_whatever_close_says()
    {
        // One triangle of the front face turned over. Its neighbours now traverse their shared
        // edges the same way it does, so the two disagree about which side is out, and there is
        // no inside anywhere near them. Filling holes cannot repair that and must not pretend
        // to: a mesh repaired into a definite but wrong inside is worse than one refused.
        foreach (string body in new[]
                 {
                     "mesh { file: \"flipped.obj\" }",
                     "mesh { file: \"flipped.obj\", close: true }",
                 })
        {
            string message = OneError(body, ("flipped.obj", FlippedCubeObj()));

            Assert.Contains("wound inconsistently", message);
            Assert.Contains("'close' cannot repair", message);
        }
    }

    [Fact]
    public void Refuses_a_non_manifold_edge()
    {
        // A ninth vertex hung off one edge of the cube gives that edge three triangles. Parity
        // is undefined there however the ray approaches it, so this one is refused before the
        // hole count is even reached.
        string message = OneError("mesh { file: \"fin.obj\" }", ("fin.obj", FinnedCubeObj()));

        Assert.Contains("not manifold", message);
        Assert.Contains("more than two triangles", message);
    }

    [Fact]
    public void Refuses_a_format_it_does_not_read()
    {
        string message = OneError("mesh { file: \"model.ply\" }", ("model.ply", "ply\n"));

        Assert.Contains("'.ply' is not a mesh format", message);
        Assert.Contains("'.obj'", message);
        Assert.Contains("'.stl'", message);
    }

    [Fact]
    public void Points_a_missing_file_at_the_field_that_named_it()
    {
        (CompiledScene? compiled, IReadOnlyList<Diagnostic> diagnostics) =
            Attempt("mesh { file: \"absent.obj\" }", [("cube.obj", CubeObj())]);

        Assert.Null(compiled);

        Diagnostic diagnostic = diagnostics.First(d => d.Severity == DiagnosticSeverity.Error);

        Assert.Contains("absent.obj", diagnostic.Message);
        Assert.Equal("\"absent.obj\"", diagnostic.Source.GetText(diagnostic.Span));
    }

    [Fact]
    public void Resolves_a_path_against_the_scene_file_rather_than_the_working_directory()
    {
        // The rule 'import' already set. A scene in one folder naming a model in a folder beside
        // it has to keep working wherever the renderer was started from, which is not something
        // a relative path resolved against the process can promise.
        CompiledScene scene = CompiledWith(
            "mesh { file: \"models/cube.obj\" }",
            ("models/cube.obj", CubeObj()));

        Assert.Equal(PrimitiveKind.Mesh, KindOf(scene, 0));
    }

    [Fact]
    public void Uploads_normals_only_when_the_scene_asks_to_be_shaded_smooth()
    {
        CompiledScene faceted = CompiledWith("mesh { file: \"cube.obj\" }", ("cube.obj", CubeObj()));
        CompiledScene smooth = CompiledWith(
            "mesh { file: \"cube.obj\", smooth: true }",
            ("cube.obj", CubeObj()));

        Assert.Equal(0f, HeaderOf(faceted, 0)[2]);
        Assert.Equal(1f, HeaderOf(smooth, 0)[2]);

        // One texel per vertex, and nothing at all when the flag is off: an unread normal is
        // bandwidth spent on nothing, which is the same argument that keeps UVs out for now.
        Assert.Equal(
            (VertexCountOf(smooth, 0) * GpuLayout.ShapeStride) + faceted.Shapes.Length,
            smooth.Shapes.Length);
    }

    [Fact]
    public void Lays_the_shape_block_out_as_the_shader_reads_it()
    {
        CompiledScene scene = CompiledWith("mesh { file: \"cube.obj\" }", ("cube.obj", CubeObj()));

        float[] head = HeaderOf(scene, 0);
        float[] bases = TexelOf(scene, (int)ParamAOf(scene, 0) + 1);

        int triangles = (int)head[0];
        int nodes = (int)head[1];

        Assert.Equal(12, triangles);

        // One triangle per leaf, so a tree over n of them has 2n - 1 nodes. That is not an
        // implementation detail the shader may assume, but it is the property that makes the
        // block's size predictable, and it is what the offsets below have to agree with.
        Assert.Equal((2 * triangles) - 1, nodes);

        Assert.Equal(2f, bases[0]);                            // nodes follow the two header texels
        Assert.Equal(2f + (nodes * 2), bases[1]);              // then one texel per triangle
        Assert.Equal(2f + (nodes * 2) + triangles, bases[2]);  // then one per vertex
        Assert.Equal(0f, bases[3]);                            // and no normals: smooth is off
    }

    [Fact]
    public void Writes_a_bvh_whose_escape_indices_walk_the_whole_tree()
    {
        // The shader carries one int and no stack, so a node's escape has to be the index of the
        // next node outside its subtree, and the walk has to terminate from any starting point.
        // A tree that does not hold this renders as a hang rather than as a wrong picture.
        CompiledScene scene = CompiledWith("mesh { file: \"cube.obj\" }", ("cube.obj", CubeObj()));

        int at = (int)ParamAOf(scene, 0);
        int nodes = (int)HeaderOf(scene, 0)[1];
        int nodeAt = at + (int)TexelOf(scene, at + 1)[0];

        int leaves = 0;

        for (int node = 0; node < nodes; node++)
        {
            float escape = TexelOf(scene, nodeAt + (node * 2))[3];
            int item = (int)TexelOf(scene, nodeAt + (node * 2) + 1)[3];

            Assert.True(escape > node, $"node {node} escapes backwards to {escape}");
            Assert.True(escape <= nodes, $"node {node} escapes past the tree to {escape}");

            if (item >= 0)
            {
                leaves++;
                Assert.True(item < 12, $"leaf {node} names triangle {item}");
            }
        }

        Assert.Equal(12, leaves);
    }

    [Fact]
    public void Tells_two_different_meshes_apart()
    {
        // The one failure this primitive can produce that would be silent. Two roots are decided
        // to be one shape by comparing the GLSL they emit, and a mesh's geometry is not in its
        // GLSL: the body carries an offset into a buffer, and inside the probe that computes the
        // comparison every buffer starts empty, so both meshes emit offset zero. Without the
        // signature they compare equal and the second is drawn as the first.
        CompiledScene scene = CompiledWith(
            """
            mesh { file: "cube.obj" }
            mesh { file: "tetra.obj", translate: [4, 0, 0] }
            """,
            ("cube.obj", CubeObj()),
            ("tetra.obj", TetrahedronObj()));

        Assert.Equal(12f, ParamBOf(scene, 0));
        Assert.Equal(4f, ParamBOf(scene, 1));

        // Two distinct blocks in the buffer, and two distinct bodies in the source.
        Assert.NotEqual(ParamAOf(scene, 0), ParamAOf(scene, 1));

        string[] signatures =
        [
            .. Regex.Matches(scene.Geometry, "// mesh ([0-9a-f]+)")
                .Select(match => match.Groups[1].Value)
                .Distinct(),
        ];

        Assert.Equal(2, signatures.Length);
    }

    [Fact]
    public void Uploads_one_copy_of_a_mesh_two_leaves_share()
    {
        CompiledScene one = CompiledWith("mesh { file: \"cube.obj\" }", ("cube.obj", CubeObj()));

        CompiledScene two = CompiledWith(
            """
            mesh { file: "cube.obj" }
            mesh { file: "cube.obj", translate: [4, 0, 0] }
            """,
            ("cube.obj", CubeObj()));

        // A contour is a handful of texels and a bunny is a hundred thousand, so the difference
        // between interning a mesh block and appending one per placement is the difference
        // between a scene that fits on the card and one that does not.
        Assert.Equal(one.Shapes.Length, two.Shapes.Length);
        Assert.Equal(ParamAOf(two, 0), ParamAOf(two, 1));
    }

    [Fact]
    public void Costs_the_same_however_many_triangles_it_holds()
    {
        // The claim the whole primitive rests on. The traversal loop takes its bound from the
        // shape buffer rather than from a literal, so the driver compiles one tree step instead
        // of one per node, and iteration 15 counts a loop bounded by a runtime value at a
        // constant. Break that and a mesh silently becomes the most expensive thing in a scene.
        Assert.True(
            TexelsOf(Grid(6)) > TexelsOf(Grid(2)) * 4,
            "the two meshes must differ in size for this to mean anything");

        Assert.Equal(CostOfShape(Grid(2)), CostOfShape(Grid(6)));
    }

    [Fact]
    public void Is_refused_by_the_distance_field_backend()
    {
        // Not an oversight. The exact distance to a triangle mesh is a second walk of the same
        // tree with its own nearest-point test plus a sign, and that backend exists to compare
        // two answers to one question rather than to render production images.
        string scene = TestSource.Camera + "mesh { file: \"cube.obj\" }";
        string directory = Directory.CreateTempSubdirectory("chroma-mesh-").FullName;

        try
        {
            File.WriteAllText(Path.Combine(directory, "cube.obj"), CubeObj());
            File.WriteAllText(Path.Combine(directory, "scene.chroma"), scene);

            SceneLoader.TryLoadCompiled(
                Path.Combine(directory, "scene.chroma"),
                out CompiledScene? compiled,
                out IReadOnlyList<Diagnostic> diagnostics,
                GeometryBackend.DistanceField);

            Assert.Null(compiled);
            Assert.Contains(diagnostics, d => d.Message.Contains("'mesh' is not supported by --sdf"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // --- Building meshes -------------------------------------------------------------------

    /// <summary>
    /// One <c>v</c> line, written the way a file on disk is written.
    /// </summary>
    /// <remarks>
    /// Invariant, and not incidentally: <c>StringBuilder.Append(float)</c> follows the current
    /// culture, so on a machine that writes a decimal comma this would emit <c>0,5</c> and the
    /// reader would refuse its own test data. The reader parses invariant, which is what a file
    /// format requires, so the fixtures have to be written that way too.
    /// </remarks>
    private static void Vertex(StringBuilder text, float x, float y, float z) =>
        text.Append(CultureInfo.InvariantCulture, $"v {x} {y} {z}\n");

    /// <summary>The cube as an OBJ, optionally missing one of its six faces.</summary>
    private static string CubeObj(int skipFace = -1)
    {
        var text = new StringBuilder();

        foreach ((float x, float y, float z) in CubeVertices)
        {
            Vertex(text, x, y, z);
        }

        for (int face = 0; face < CubeFaces.Length; face++)
        {
            if (face == skipFace)
            {
                continue;
            }

            text.Append('f');

            foreach (int corner in CubeFaces[face])
            {
                text.Append(' ').Append(corner);
            }

            text.Append('\n');
        }

        return text.ToString();
    }

    /// <summary>The cube with one triangle of its front face turned over.</summary>
    private static string FlippedCubeObj() =>
        CubeObj().Replace("f 5 6 7 8\n", "f 5 7 6\nf 5 7 8\n", StringComparison.Ordinal);

    /// <summary>The cube with a stray triangle hung off the edge 5 to 6.</summary>
    private static string FinnedCubeObj() =>
        CubeObj() + "v 0.5 -1 2\nf 5 6 9\n";

    private static string TetrahedronObj() =>
        """
        v 0 0 0
        v 1 0 0
        v 0 1 0
        v 0 0 1
        f 1 3 2
        f 1 2 4
        f 1 4 3
        f 2 3 4
        """;

    /// <summary>
    /// A closed grid of <c>n</c> by <c>n</c> quads on each of the cube's six faces, as one OBJ.
    /// </summary>
    /// <remarks>
    /// Built by subdividing rather than by writing a bigger model out, because the only property
    /// the cost test needs is that one of these holds many more triangles than the other while
    /// being the same kind of thing.
    /// </remarks>
    private static string Grid(int n)
    {
        var vertices = new List<(float X, float Y, float Z)>();
        var faces = new List<int[]>();
        Dictionary<(float, float, float), int> seen = [];

        int Index(float x, float y, float z)
        {
            if (!seen.TryGetValue((x, y, z), out int at))
            {
                vertices.Add((x, y, z));
                at = vertices.Count;
                seen[(x, y, z)] = at;
            }

            return at;
        }

        // Each face is a plane at 0 or 1 on one axis, gridded over the other two. The winding is
        // taken from the whole-face quad this subdivides, so the result is closed and consistent
        // by construction rather than by being written out correctly six times.
        for (int axis = 0; axis < 3; axis++)
        {
            for (int side = 0; side < 2; side++)
            {
                for (int i = 0; i < n; i++)
                {
                    for (int j = 0; j < n; j++)
                    {
                        float u0 = (float)i / n;
                        float u1 = (float)(i + 1) / n;
                        float v0 = (float)j / n;
                        float v1 = (float)(j + 1) / n;

                        (float, float, float) At(float u, float v) => axis switch
                        {
                            0 => (side, u, v),
                            1 => (v, side, u),
                            _ => (u, v, side),
                        };

                        int a = Index(At(u0, v0).Item1, At(u0, v0).Item2, At(u0, v0).Item3);
                        int b = Index(At(u1, v0).Item1, At(u1, v0).Item2, At(u1, v0).Item3);
                        int c = Index(At(u1, v1).Item1, At(u1, v1).Item2, At(u1, v1).Item3);
                        int d = Index(At(u0, v1).Item1, At(u0, v1).Item2, At(u0, v1).Item3);

                        faces.Add(side == 1 ? [a, b, c, d] : [a, d, c, b]);
                    }
                }
            }
        }

        var text = new StringBuilder();

        foreach ((float x, float y, float z) in vertices)
        {
            Vertex(text, x, y, z);
        }

        foreach (int[] face in faces)
        {
            text.Append('f');

            foreach (int corner in face)
            {
                text.Append(' ').Append(corner);
            }

            text.Append('\n');
        }

        return text.ToString();
    }

    /// <summary>The cube's twelve triangles, as a binary STL with no shared vertices.</summary>
    private static byte[] BinaryStl()
    {
        var stream = new MemoryStream();
        var writer = new BinaryWriter(stream);

        writer.Write(new byte[80]);
        writer.Write((uint)(CubeFaces.Length * 2));

        foreach ((int a, int b, int c) in CubeTriangles())
        {
            // The facet normal, which the reader discards: it says nothing the winding does not.
            writer.Write(0f);
            writer.Write(0f);
            writer.Write(0f);

            foreach (int corner in new[] { a, b, c })
            {
                (float x, float y, float z) = CubeVertices[corner];
                writer.Write(x);
                writer.Write(y);
                writer.Write(z);
            }

            writer.Write((ushort)0);
        }

        writer.Flush();

        return stream.ToArray();
    }

    private static string AsciiStl()
    {
        var text = new StringBuilder("solid cube\n");

        foreach ((int a, int b, int c) in CubeTriangles())
        {
            text.Append("  facet normal 0 0 0\n    outer loop\n");

            foreach (int corner in new[] { a, b, c })
            {
                (float x, float y, float z) = CubeVertices[corner];
                text.Append(CultureInfo.InvariantCulture, $"      vertex {x} {y} {z}\n");
            }

            text.Append("    endloop\n  endfacet\n");
        }

        return text.Append("endsolid cube\n").ToString();
    }

    /// <summary>The cube's faces fanned into triangles, as zero-based corner indices.</summary>
    private static IEnumerable<(int A, int B, int C)> CubeTriangles()
    {
        foreach (int[] face in CubeFaces)
        {
            for (int i = 1; i + 1 < face.Length; i++)
            {
                yield return (face[0] - 1, face[i] - 1, face[i + 1] - 1);
            }
        }
    }

    // --- Driving the loader ----------------------------------------------------------------

    private static CompiledScene CompiledWith(string body, params (string Name, string Text)[] assets)
    {
        (CompiledScene? compiled, IReadOnlyList<Diagnostic> diagnostics) = Attempt(body, assets);

        Assert.True(
            compiled is not null,
            "expected a compiled scene, got: " + string.Join("; ", diagnostics.Select(d => d.Message)));

        return compiled!;
    }

    private static CompiledScene CompiledWith(string body, (string Name, byte[] Bytes) asset)
    {
        (CompiledScene? compiled, IReadOnlyList<Diagnostic> diagnostics) =
            Attempt(body, [], [asset]);

        Assert.True(
            compiled is not null,
            "expected a compiled scene, got: " + string.Join("; ", diagnostics.Select(d => d.Message)));

        return compiled!;
    }

    /// <summary>The single error a failing scene reports, asserting that there is exactly one.</summary>
    private static string OneError(string body, params (string Name, string Text)[] assets)
    {
        (CompiledScene? compiled, IReadOnlyList<Diagnostic> diagnostics) = Attempt(body, assets);

        Assert.Null(compiled);

        // One and only one: a mesh that is not a solid is a single fact about a single file, and
        // a diagnostic per broken edge would bury the sentence worth reading.
        Diagnostic diagnostic = Assert.Single(
            diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));

        return diagnostic.Message;
    }

    private static (CompiledScene? Compiled, IReadOnlyList<Diagnostic> Diagnostics) Attempt(
        string body,
        (string Name, string Text)[] assets,
        (string Name, byte[] Bytes)[]? binaries = null)
    {
        string directory = Directory.CreateTempSubdirectory("chroma-mesh-").FullName;

        try
        {
            foreach ((string name, string text) in assets)
            {
                Write(directory, name, Encoding.UTF8.GetBytes(text));
            }

            foreach ((string name, byte[] bytes) in binaries ?? [])
            {
                Write(directory, name, bytes);
            }

            string scene = Path.Combine(directory, "scene.chroma");
            File.WriteAllText(scene, TestSource.Camera + body);

            SceneLoader.TryLoadCompiled(
                scene, out CompiledScene? compiled, out IReadOnlyList<Diagnostic> diagnostics);

            return (compiled, diagnostics);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void Write(string directory, string name, byte[] bytes)
    {
        string path = Path.Combine(directory, name);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    /// <summary>What one appearance of this mesh weighs, in the units of <see cref="ShapeCost"/>.</summary>
    private static int CostOfShape(string obj) =>
        Assert.Single(CompiledWith("mesh { file: \"m.obj\" }", ("m.obj", obj)).ShapeReports).Cost;

    /// <summary>How many texels this mesh occupies in the shape buffer.</summary>
    private static int TexelsOf(string obj) =>
        CompiledWith("mesh { file: \"m.obj\" }", ("m.obj", obj)).Shapes.Length
        / GpuLayout.ShapeStride;

    // --- Reading the tables ----------------------------------------------------------------

    private static PrimitiveKind KindOf(CompiledScene scene, int primitive) =>
        (PrimitiveKind)(int)scene.Primitives[primitive * GpuLayout.PrimitiveStride];

    private static float ParamAOf(CompiledScene scene, int primitive) =>
        scene.Primitives[(primitive * GpuLayout.PrimitiveStride) + 2];

    private static float ParamBOf(CompiledScene scene, int primitive) =>
        scene.Primitives[(primitive * GpuLayout.PrimitiveStride) + 3];

    private static float[] HeaderOf(CompiledScene scene, int primitive) =>
        TexelOf(scene, (int)ParamAOf(scene, primitive));

    private static float[] TexelOf(CompiledScene scene, int texel) =>
        scene.Shapes[(texel * GpuLayout.ShapeStride)..((texel + 1) * GpuLayout.ShapeStride)];

    /// <summary>
    /// How many vertices the block holds: the run between where the vertices start and whatever
    /// comes after them, which is the normals when there are any and the end of the buffer when
    /// there are not.
    /// </summary>
    /// <remarks>
    /// The count is not written down anywhere, and deliberately: the shader never needs it,
    /// because a triangle names its three corners and nothing walks the vertices in order.
    /// </remarks>
    private static int VertexCountOf(CompiledScene scene, int primitive)
    {
        int at = (int)ParamAOf(scene, primitive);
        float[] bases = TexelOf(scene, at + 1);
        bool smooth = HeaderOf(scene, primitive)[2] > 0.5f;

        int end = smooth ? at + (int)bases[3] : scene.Shapes.Length / GpuLayout.ShapeStride;

        return end - at - (int)bases[2];
    }

}
