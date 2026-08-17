using System.Globalization;
using System.Numerics;

namespace Chroma.Core.Assets;

/// <summary>
/// Turns a triangle mesh into a <b>solid</b>, or explains why it is not one.
/// </summary>
/// <remarks>
/// <para>
/// This is the piece that makes <c>mesh</c> a CSG operand rather than a picture of one. Every
/// shape in this renderer needs a well-defined inside, which is the rule that refused POV-Ray's
/// open cones in iteration 6, and the inside of a mesh is defined by the parity of a ray's
/// crossings. Parity only means something on a surface that is <b>closed</b>, <b>manifold</b> and
/// <b>consistently oriented</b>, and whether a file is any of those three is a question about the
/// file rather than about a field. Asking it is the work.
/// </para>
/// <para>
/// <b>One test answers all three.</b> Take every triangle's three edges as <i>directed</i> pairs,
/// in winding order. A surface with those properties has each directed edge exactly once, and its
/// reverse exactly once, because the two triangles sharing an edge traverse it in opposite
/// directions. So the three failures are three readings of the same table:
/// </para>
/// <list type="bullet">
/// <item><description>a directed edge whose reverse is missing is a <b>hole</b>;</description></item>
/// <item><description>a directed edge that appears twice is <b>inconsistent winding</b>, two
/// neighbouring triangles disagreeing about which side is out;</description></item>
/// <item><description>an edge with more than two triangles on it is <b>non-manifold</b>, and has
/// no inside anywhere near it.</description></item>
/// </list>
/// <para>
/// <b>Only the hole is repairable, and only on request.</b> <c>close: true</c> fills each
/// boundary loop with a fan, because the two models this iteration was written for — the Utah
/// teapot, open at the rim, and the Stanford bunny, open underneath — are otherwise perfectly
/// good surfaces that simply are not solids. The other two failures are refused whatever
/// <c>close</c> says: filling a hole in a mesh whose triangles disagree about which side is out
/// produces something whose inside is undefined either way, and repairing it silently would be
/// the renderer guessing at what the file meant.
/// </para>
/// </remarks>
internal static class MeshTopology
{
    /// <summary>
    /// The welded, checked and optionally closed mesh, or null having set
    /// <paramref name="error"/> to what is wrong with it.
    /// </summary>
    /// <param name="close">Whether to fill boundary loops rather than refuse them.</param>
    /// <param name="smooth">
    /// Whether the result needs a normal per vertex. Derived here rather than in the readers
    /// because it is the scene, not the file, that decides whether a mesh is shaded smooth.
    /// </param>
    public static MeshData? Build(MeshData source, bool close, bool smooth, out string? error)
    {
        MeshData welded = Weld(source);

        if (welded.TriangleCount == 0)
        {
            error = "every triangle is degenerate: no two of its corners are distinct";
            return null;
        }

        Dictionary<long, int> directed = CountEdges(welded.Indices);

        if (!Classify(welded, directed, out List<long>? boundary, out error))
        {
            return null;
        }

        if (boundary!.Count > 0)
        {
            if (!close)
            {
                error = Describe(welded, boundary);
                return null;
            }

            welded = Close(welded, boundary);

            // The fan is not a proof. A boundary that pinches through one vertex chains into a
            // figure of eight, and a single fan over it repeats a directed edge, so the answer is
            // checked again rather than assumed.
            if (!Classify(welded, CountEdges(welded.Indices), out boundary, out error))
            {
                return null;
            }

            if (boundary!.Count > 0)
            {
                error = "'close' could not close this mesh: " + Describe(welded, boundary);
                return null;
            }
        }

        error = null;

        return smooth ? welded with { Normals = Normals(welded) } : welded with { Normals = null };
    }

    /// <summary>Normalised, or zero for a vector too short to have a direction.</summary>
    public static Vector3 SafeNormalize(Vector3 v)
    {
        float length = v.Length();

        return length > 1e-20f ? v / length : Vector3.Zero;
    }

    /// <summary>
    /// Positions merged by exact value, indices remapped onto them, degenerate triangles dropped.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Mandatory for STL, which writes every corner of every facet in full and so shares nothing
    /// with anything; without this step every edge of every STL is a boundary edge and every STL
    /// is refused. Harmless for OBJ, which already indexes its vertices, except where a file
    /// happens to repeat one.
    /// </para>
    /// <para>
    /// <b>Exact equality, not a tolerance.</b> A tolerance needs a scale, and the only scale
    /// available is the model's own bounding box, which would make welding depend on how far from
    /// the origin the file happened to be written. Every writer that emits an STL from a mesh
    /// emits the same bits for the same shared vertex, so exact is what matches, and a file where
    /// it does not is a file with genuinely distinct points a hair apart.
    /// </para>
    /// </remarks>
    private static MeshData Weld(MeshData source)
    {
        Dictionary<Vector3, int> seen = new(source.Positions.Count);
        int[] map = new int[source.Positions.Count];
        List<Vector3> positions = new(source.Positions.Count);

        for (int i = 0; i < source.Positions.Count; i++)
        {
            Vector3 position = source.Positions[i];

            if (!seen.TryGetValue(position, out int slot))
            {
                slot = positions.Count;
                seen[position] = slot;
                positions.Add(position);
            }

            map[i] = slot;
        }

        List<int> indices = new(source.Indices.Count);

        for (int t = 0; t + 2 < source.Indices.Count; t += 3)
        {
            int a = map[source.Indices[t]];
            int b = map[source.Indices[t + 1]];
            int c = map[source.Indices[t + 2]];

            // Two corners in the same place leave a triangle with no area, two of whose edges are
            // each other's reverse. It bounds nothing and would be counted as a surface here.
            if (a == b || b == c || c == a)
            {
                continue;
            }

            indices.Add(a);
            indices.Add(b);
            indices.Add(c);
        }

        if (positions.Count == source.Positions.Count && indices.Count == source.Indices.Count)
        {
            return source;
        }

        return new MeshData(positions, indices, Remap(source.Normals, map, positions.Count));
    }

    /// <summary>Per-vertex normals carried through the weld, averaged where vertices merged.</summary>
    private static Vector3[]? Remap(IReadOnlyList<Vector3>? normals, int[] map, int count)
    {
        if (normals is null)
        {
            return null;
        }

        Vector3[] result = new Vector3[count];

        for (int i = 0; i < map.Length; i++)
        {
            result[map[i]] += normals[i];
        }

        for (int i = 0; i < count; i++)
        {
            result[i] = SafeNormalize(result[i]);
        }

        return result;
    }

    /// <summary>How many times each directed edge appears, keyed by its two endpoints.</summary>
    private static Dictionary<long, int> CountEdges(IReadOnlyList<int> indices)
    {
        Dictionary<long, int> directed = new(indices.Count);

        for (int t = 0; t + 2 < indices.Count; t += 3)
        {
            Bump(directed, indices[t], indices[t + 1]);
            Bump(directed, indices[t + 1], indices[t + 2]);
            Bump(directed, indices[t + 2], indices[t]);
        }

        return directed;
    }

    private static void Bump(Dictionary<long, int> counts, int from, int to)
    {
        long key = Key(from, to);
        counts[key] = counts.TryGetValue(key, out int count) ? count + 1 : 1;
    }

    private static long Key(int from, int to) => ((long)from << 32) | (uint)to;

    private static int From(long key) => (int)(key >> 32);

    private static int To(long key) => (int)(key & 0xFFFFFFFF);

    /// <summary>
    /// Reads the edge table for the two failures that cannot be repaired, and collects the
    /// boundary edges for the one that can.
    /// </summary>
    /// <remarks>
    /// Reported in order of how structural the failure is rather than in the order the table
    /// happens to be walked in, and one at a time: a mesh that is non-manifold is usually full of
    /// holes as well, and the hole count is not the thing to fix first.
    /// </remarks>
    private static bool Classify(
        MeshData mesh,
        Dictionary<long, int> directed,
        out List<long>? boundary,
        out string? error)
    {
        boundary = null;
        error = null;

        long nonManifold = -1;
        long doubled = -1;
        int nonManifoldCount = 0;
        int doubledCount = 0;
        List<long> holes = [];

        foreach ((long key, int count) in directed)
        {
            directed.TryGetValue(Key(To(key), From(key)), out int reverse);

            if (count + reverse > 2)
            {
                nonManifoldCount++;
                nonManifold = nonManifold < 0 ? key : nonManifold;
                continue;
            }

            if (count > 1)
            {
                doubledCount++;
                doubled = doubled < 0 ? key : doubled;
                continue;
            }

            if (reverse == 0)
            {
                holes.Add(key);
            }
        }

        if (nonManifoldCount > 0)
        {
            error = $"the mesh is not manifold: {Plural(nonManifoldCount, "edge")} "
                + $"joined by more than two triangles, the first at {At(mesh, nonManifold)}. "
                + "An edge with three faces on it has no inside near it, so no CSG solid can be "
                + "made of this file";
            return false;
        }

        if (doubledCount > 0)
        {
            error = $"the mesh is wound inconsistently: {Plural(doubledCount, "edge")} "
                + $"traversed the same way by both of its triangles, the first at {At(mesh, doubled)}. "
                + "Neighbouring triangles disagree about which side is out, and 'close' cannot "
                + "repair that";
            return false;
        }

        boundary = holes;

        return true;
    }

    private static string Describe(MeshData mesh, List<long> boundary) =>
        $"the mesh is not closed: {Plural(boundary.Count, "boundary edge")}, "
        + $"the first at {At(mesh, boundary[0])}. A CSG solid needs a well-defined inside; "
        + "write 'close: true' to fill its holes";

    private static string Plural(int count, string noun) =>
        count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    /// <summary>Where an edge is, as the position of the vertex it starts at.</summary>
    private static string At(MeshData mesh, long key)
    {
        Vector3 p = mesh.Positions[From(key)];

        return string.Create(
            CultureInfo.InvariantCulture,
            $"({p.X:0.####}, {p.Y:0.####}, {p.Z:0.####})");
    }

    /// <summary>
    /// Fills every hole with a fan from its own centroid.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The holes are found by joining boundary edges that share a vertex, rather than by walking
    /// each loop in order: the grouping is all the fan needs, and a walk has to decide what to do
    /// at a vertex where two boundaries touch, where the grouping simply puts them in the same
    /// hole.
    /// </para>
    /// <para>
    /// A boundary edge <c>a to b</c> is missing its reverse, so the triangle that repairs it is
    /// <c>(centre, b, a)</c>: that supplies <c>b to a</c>, and the two spokes <c>centre to b</c>
    /// and <c>a to centre</c> cancel against the neighbouring triangles' own spokes all the way
    /// round. One new vertex per hole, at the average of its boundary.
    /// </para>
    /// </remarks>
    private static MeshData Close(MeshData mesh, List<long> boundary)
    {
        Dictionary<int, int> group = [];
        List<List<long>> holes = [];

        foreach (long edge in boundary)
        {
            int a = From(edge);
            int b = To(edge);

            bool knownA = group.TryGetValue(a, out int ga);
            bool knownB = group.TryGetValue(b, out int gb);

            int hole;

            if (knownA && knownB && ga != gb)
            {
                // The edge bridges two runs found separately: fold the later into the earlier.
                (int keep, int drop) = ga < gb ? (ga, gb) : (gb, ga);

                holes[keep].AddRange(holes[drop]);
                holes[drop].Clear();

                foreach (int vertex in group.Keys.Where(v => group[v] == drop).ToList())
                {
                    group[vertex] = keep;
                }

                hole = keep;
            }
            else if (knownA)
            {
                hole = ga;
            }
            else if (knownB)
            {
                hole = gb;
            }
            else
            {
                hole = holes.Count;
                holes.Add([]);
            }

            holes[hole].Add(edge);
            group[a] = hole;
            group[b] = hole;
        }

        List<Vector3> positions = [.. mesh.Positions];
        List<int> indices = [.. mesh.Indices];

        foreach (List<long> hole in holes)
        {
            if (hole.Count == 0)
            {
                continue;
            }

            Vector3 centre = Vector3.Zero;

            foreach (long edge in hole)
            {
                centre += mesh.Positions[From(edge)];
            }

            int centreIndex = positions.Count;
            positions.Add(centre / hole.Count);

            foreach (long edge in hole)
            {
                indices.Add(centreIndex);
                indices.Add(To(edge));
                indices.Add(From(edge));
            }
        }

        return new MeshData(positions, indices, Extend(mesh.Normals, positions.Count));
    }

    /// <summary>
    /// Normals lengthened to cover the vertices a cap added, leaving the new ones at zero.
    /// </summary>
    /// <remarks>
    /// A cap's centre has no normal from the file, and giving it one from the fan would be
    /// pretending the file said something about a surface it does not contain.
    /// <see cref="Normals"/> derives every zero-length entry from the triangles around it, which
    /// is the right answer for a flat cap and costs nothing to reach.
    /// </remarks>
    private static Vector3[]? Extend(IReadOnlyList<Vector3>? normals, int count)
    {
        if (normals is null)
        {
            return null;
        }

        Vector3[] result = new Vector3[count];

        for (int i = 0; i < normals.Count; i++)
        {
            result[i] = normals[i];
        }

        return result;
    }

    /// <summary>
    /// A normal per vertex: the file's where it gave one, and the area-weighted average of the
    /// incident faces where it did not.
    /// </summary>
    /// <remarks>
    /// Area weighting falls out of not normalising the cross product, whose length is twice the
    /// triangle's area. That is the weighting worth having: a fine triangle and a coarse one
    /// meeting at a vertex describe the same surface, and letting the coarse one count for more
    /// is what makes an unevenly tessellated model shade evenly.
    /// </remarks>
    private static Vector3[] Normals(MeshData mesh)
    {
        Vector3[] derived = new Vector3[mesh.Positions.Count];

        for (int t = 0; t + 2 < mesh.Indices.Count; t += 3)
        {
            int a = mesh.Indices[t];
            int b = mesh.Indices[t + 1];
            int c = mesh.Indices[t + 2];

            Vector3 face = Vector3.Cross(
                mesh.Positions[b] - mesh.Positions[a],
                mesh.Positions[c] - mesh.Positions[a]);

            derived[a] += face;
            derived[b] += face;
            derived[c] += face;
        }

        IReadOnlyList<Vector3>? given = mesh.Normals;

        for (int i = 0; i < derived.Length; i++)
        {
            Vector3 supplied = given is not null && i < given.Count ? given[i] : Vector3.Zero;

            derived[i] = supplied.LengthSquared() > 0f ? SafeNormalize(supplied) : SafeNormalize(derived[i]);
        }

        return derived;
    }
}
