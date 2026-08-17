using System.Globalization;
using System.Numerics;

namespace Chroma.Core.Assets;

/// <summary>
/// Wavefront OBJ, the text half of the two formats this reads.
/// </summary>
/// <remarks>
/// <para>
/// Four line kinds matter and the rest are read past: <c>v</c>, <c>vn</c>, <c>vt</c> and
/// <c>f</c>. <c>g</c>, <c>o</c>, <c>s</c>, <c>usemtl</c> and <c>mtllib</c> all describe how a
/// mesh is grouped or painted, and this primitive has one material and one shape, so they are
/// skipped rather than refused: refusing them would reject most of the models anyone has.
/// </para>
/// <para>
/// <b>Normals are folded onto positions.</b> OBJ indexes normals separately, so one position may
/// be quoted with several, and the obvious reading — one vertex per distinct <c>v/vn</c> pair —
/// tears the mesh apart topologically: two triangles across a hard edge would stop sharing their
/// vertices, every such edge would count as a boundary, and a perfectly closed model would be
/// refused. So the topology is the <b>positions</b>, and a position's normal is the average of
/// every normal quoted for it. Where a file gives one normal per position, which is what a
/// smoothed model does, that reproduces the file exactly; where it gives several, averaging them
/// is what <c>smooth: true</c> was asking for in the first place.
/// </para>
/// <para>
/// A face with more than three vertices is triangulated as a fan from its first vertex. That is
/// correct for a convex face and is what every OBJ consumer does; a concave quad is rare enough,
/// and visible enough when it goes wrong, not to be worth an ear-clipping pass here.
/// </para>
/// </remarks>
internal static class ObjReader
{
    /// <summary>
    /// Parses OBJ text, or returns null having set <paramref name="error"/> to one sentence
    /// naming the line that failed.
    /// </summary>
    public static MeshData? Read(string text, out string? error)
    {
        List<Vector3> positions = [];
        List<Vector3> normals = [];
        List<Vector3> vertexNormals = [];
        List<int> normalCounts = [];
        List<int> indices = [];
        List<int> face = [];

        bool sawNormal = false;
        int line = 0;

        foreach (ReadOnlyMemory<char> raw in Lines(text))
        {
            line++;

            ReadOnlySpan<char> content = Strip(raw.Span);

            if (content.IsEmpty)
            {
                continue;
            }

            ReadOnlySpan<char> keyword = NextToken(ref content);

            if (keyword.SequenceEqual("v"))
            {
                if (!ReadVector(ref content, out Vector3 position))
                {
                    error = $"line {line}: 'v' needs three numbers";
                    return null;
                }

                positions.Add(position);
                vertexNormals.Add(Vector3.Zero);
                normalCounts.Add(0);
                continue;
            }

            if (keyword.SequenceEqual("vn"))
            {
                if (!ReadVector(ref content, out Vector3 normal))
                {
                    error = $"line {line}: 'vn' needs three numbers";
                    return null;
                }

                normals.Add(normal);
                continue;
            }

            if (!keyword.SequenceEqual("f"))
            {
                // vt, g, o, s, usemtl, mtllib and anything else this does not model.
                continue;
            }

            face.Clear();

            while (true)
            {
                ReadOnlySpan<char> token = NextToken(ref content);

                if (token.IsEmpty)
                {
                    break;
                }

                if (!ReadReference(token, positions.Count, normals.Count, out int v, out int vn))
                {
                    error = $"line {line}: '{token}' is not a usable face vertex";
                    return null;
                }

                face.Add(v);

                if (vn < 0)
                {
                    continue;
                }

                sawNormal = true;
                vertexNormals[v] += normals[vn];
                normalCounts[v]++;
            }

            if (face.Count < 3)
            {
                error = $"line {line}: 'f' needs at least three vertices, found {face.Count}";
                return null;
            }

            // A fan from the first vertex. Every triangle keeps the polygon's winding, which is
            // what the closedness check downstream is about to read.
            for (int i = 1; i + 1 < face.Count; i++)
            {
                indices.Add(face[0]);
                indices.Add(face[i]);
                indices.Add(face[i + 1]);
            }
        }

        if (indices.Count == 0)
        {
            error = "no faces: an 'obj' file with no 'f' line describes no surface";
            return null;
        }

        error = null;

        return new MeshData(positions, indices, sawNormal ? Average(vertexNormals, normalCounts) : null);
    }

    /// <summary>
    /// The accumulated normals divided back down, with a zero-length result left at zero for
    /// <see cref="MeshTopology"/> to replace with a derived one.
    /// </summary>
    private static Vector3[] Average(List<Vector3> sums, List<int> counts)
    {
        Vector3[] result = new Vector3[sums.Count];

        for (int i = 0; i < sums.Count; i++)
        {
            result[i] = counts[i] == 0 ? Vector3.Zero : MeshTopology.SafeNormalize(sums[i]);
        }

        return result;
    }

    /// <summary>
    /// One face vertex, as <c>v</c>, <c>v/vt</c>, <c>v//vn</c> or <c>v/vt/vn</c>, resolved to
    /// zero-based indices. <paramref name="normal"/> comes back negative when none was given.
    /// </summary>
    /// <remarks>
    /// A negative index in the file counts back from the end of what has been declared so far,
    /// which is the one part of the format that cannot be resolved in a second pass: the meaning
    /// of -1 depends on how many vertices preceded the face.
    /// </remarks>
    private static bool ReadReference(
        ReadOnlySpan<char> token,
        int positionCount,
        int normalCount,
        out int position,
        out int normal)
    {
        position = -1;
        normal = -1;

        int firstSlash = token.IndexOf('/');
        ReadOnlySpan<char> positionText = firstSlash < 0 ? token : token[..firstSlash];

        if (!Resolve(positionText, positionCount, out position))
        {
            return false;
        }

        if (firstSlash < 0)
        {
            return true;
        }

        ReadOnlySpan<char> rest = token[(firstSlash + 1)..];
        int secondSlash = rest.IndexOf('/');

        if (secondSlash < 0)
        {
            // v/vt: a texture coordinate, which nothing reads yet.
            return true;
        }

        ReadOnlySpan<char> normalText = rest[(secondSlash + 1)..];

        return normalText.IsEmpty || Resolve(normalText, normalCount, out normal);
    }

    /// <summary>One-based, or negative counting back from the end, to zero-based.</summary>
    private static bool Resolve(ReadOnlySpan<char> text, int count, out int index)
    {
        index = -1;

        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            || value == 0)
        {
            return false;
        }

        index = value > 0 ? value - 1 : count + value;

        return index >= 0 && index < count;
    }

    private static bool ReadVector(ref ReadOnlySpan<char> content, out Vector3 vector)
    {
        vector = default;

        if (!ReadFloat(ref content, out float x)
            || !ReadFloat(ref content, out float y)
            || !ReadFloat(ref content, out float z))
        {
            return false;
        }

        vector = new Vector3(x, y, z);

        // A fourth component is legal on 'v' and is a homogeneous weight almost nobody writes.
        // Reading past it rather than dividing by it: a file that means something by it is a
        // file this would render subtly wrong either way.
        return true;
    }

    private static bool ReadFloat(ref ReadOnlySpan<char> content, out float value)
    {
        ReadOnlySpan<char> token = NextToken(ref content);

        return float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>
    /// The next whitespace-delimited token, advancing past it. Empty at the end of the line.
    /// </summary>
    private static ReadOnlySpan<char> NextToken(ref ReadOnlySpan<char> content)
    {
        content = content.TrimStart();

        if (content.IsEmpty)
        {
            return default;
        }

        int end = content.IndexOfAny(' ', '\t');

        if (end < 0)
        {
            ReadOnlySpan<char> whole = content;
            content = default;
            return whole;
        }

        ReadOnlySpan<char> token = content[..end];
        content = content[end..];

        return token;
    }

    /// <summary>The line without its comment, its carriage return or its surrounding space.</summary>
    private static ReadOnlySpan<char> Strip(ReadOnlySpan<char> line)
    {
        int comment = line.IndexOf('#');

        return (comment < 0 ? line : line[..comment]).Trim(" \t\r");
    }

    /// <summary>
    /// The lines of the text without materialising a string per line.
    /// </summary>
    /// <remarks>
    /// A million-triangle OBJ is several million lines, and <c>string.Split('\n')</c> over one
    /// allocates every one of them. This is the only reason the parser above is written against
    /// spans rather than strings.
    /// </remarks>
    private static IEnumerable<ReadOnlyMemory<char>> Lines(string text)
    {
        ReadOnlyMemory<char> rest = text.AsMemory();

        while (!rest.IsEmpty)
        {
            int end = rest.Span.IndexOf('\n');

            if (end < 0)
            {
                yield return rest;
                yield break;
            }

            yield return rest[..end];
            rest = rest[(end + 1)..];
        }
    }
}
