using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace Chroma.Core.Assets;

/// <summary>
/// STL, in both of the two encodings that share the name.
/// </summary>
/// <remarks>
/// <para>
/// <b>STL has no vertices, only corners.</b> Every facet writes its three points out in full, so
/// a cube arrives as thirty-six positions rather than eight and no two triangles share anything.
/// Nothing here fixes that, deliberately: welding is what makes a mesh a surface rather than a
/// pile of triangles, every format needs it to some degree, and one implementation of it lives in
/// <see cref="MeshTopology"/> where the closedness check that depends on it also lives.
/// </para>
/// <para>
/// <b>The per-facet normal is discarded.</b> It is a face normal, so it carries nothing the
/// winding does not already say, and smooth shading needs a normal per vertex rather than per
/// face. Where a file's stored normal disagrees with its winding it is the winding that this
/// renderer believes, because the winding is what the parity test reads.
/// </para>
/// <para>
/// <b>Which encoding a file is in is decided by arithmetic, not by its first word.</b> The
/// convention is that an ASCII file begins with <c>solid</c>, and a great many binary files put a
/// product name beginning with <c>solid</c> in their 80-byte header. The length is not
/// ambiguous: a binary file is exactly <c>84 + 50 n</c> bytes for the <c>n</c> it declares.
/// </para>
/// </remarks>
internal static class StlReader
{
    private const int HeaderBytes = 80;
    private const int CountBytes = 4;
    private const int FacetBytes = 50;

    public static MeshData? Read(byte[] bytes, out string? error) =>
        IsBinary(bytes) ? ReadBinary(bytes, out error) : ReadText(bytes, out error);

    /// <summary>Whether the byte count is exactly what the declared facet count implies.</summary>
    private static bool IsBinary(byte[] bytes)
    {
        if (bytes.Length < HeaderBytes + CountBytes)
        {
            return false;
        }

        uint facets = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(HeaderBytes));

        return (long)bytes.Length == HeaderBytes + CountBytes + ((long)facets * FacetBytes);
    }

    private static MeshData? ReadBinary(byte[] bytes, out string? error)
    {
        int facets = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(HeaderBytes));

        if (facets == 0)
        {
            error = "no facets: an 'stl' file declaring zero triangles describes no surface";
            return null;
        }

        Vector3[] positions = new Vector3[facets * 3];
        int[] indices = new int[facets * 3];
        int at = HeaderBytes + CountBytes;

        for (int f = 0; f < facets; f++)
        {
            // The facet normal occupies the first twelve bytes and is read past.
            int corner = at + 12;

            for (int c = 0; c < 3; c++)
            {
                int slot = (f * 3) + c;

                positions[slot] = new Vector3(
                    BitConverter.ToSingle(bytes, corner),
                    BitConverter.ToSingle(bytes, corner + 4),
                    BitConverter.ToSingle(bytes, corner + 8));

                indices[slot] = slot;
                corner += 12;
            }

            at += FacetBytes;
        }

        error = null;

        return new MeshData(positions, indices, null);
    }

    private static MeshData? ReadText(byte[] bytes, out string? error)
    {
        List<Vector3> positions = [];
        List<int> indices = [];

        string text = Encoding.UTF8.GetString(bytes);
        int line = 0;

        foreach (string raw in text.Split('\n'))
        {
            line++;

            ReadOnlySpan<char> content = raw.AsSpan().Trim(" \t\r");

            if (!content.StartsWith("vertex", StringComparison.OrdinalIgnoreCase))
            {
                // solid, facet, outer loop, endloop, endfacet, endsolid. The structure carries
                // nothing the vertex order does not: three vertices in a row are one triangle.
                continue;
            }

            if (!ReadVector(content["vertex".Length..], out Vector3 position))
            {
                error = $"line {line}: 'vertex' needs three numbers";
                return null;
            }

            indices.Add(positions.Count);
            positions.Add(position);
        }

        if (positions.Count == 0)
        {
            error = "no vertices: an 'stl' file with no 'vertex' line describes no surface";
            return null;
        }

        if (positions.Count % 3 != 0)
        {
            error = $"{positions.Count} vertices is not a whole number of triangles";
            return null;
        }

        error = null;

        return new MeshData(positions, indices, null);
    }

    private static bool ReadVector(ReadOnlySpan<char> content, out Vector3 vector)
    {
        vector = default;

        Span<float> parts = stackalloc float[3];

        for (int i = 0; i < 3; i++)
        {
            content = content.TrimStart(" \t");

            if (content.IsEmpty)
            {
                return false;
            }

            int end = content.IndexOfAny(' ', '\t');
            ReadOnlySpan<char> token = end < 0 ? content : content[..end];

            if (!float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out parts[i]))
            {
                return false;
            }

            content = end < 0 ? default : content[end..];
        }

        vector = new Vector3(parts[0], parts[1], parts[2]);

        return true;
    }
}
