using System.Text;

namespace Chroma.Core.Assets;

/// <summary>
/// The one way into the mesh loaders: read a file, decode it by its extension, and hand back a
/// mesh that is known to be a solid, or a sentence saying why it is not.
/// </summary>
/// <remarks>
/// <para>
/// The first thing in this solution that reads an external asset. <c>Program</c> reads the scene
/// file and the evaluator reads imported fragments; both are source text. This reads data, and it
/// follows the rule the evaluator already set for imports: a path is resolved against the file
/// that wrote it, and a failure is a diagnostic rather than an exception. Resolution itself is
/// the caller's, because only the binder holds the span to report against.
/// </para>
/// <para>
/// <b>Nothing throws.</b> A malformed file is user data, not a bug, so every failure comes back
/// as <c>error</c> and the binder decides where to point.
/// </para>
/// </remarks>
public static class MeshFile
{
    /// <summary>Extensions this understands, for a diagnostic that can list them.</summary>
    public static IReadOnlyList<string> Extensions { get; } = [".obj", ".stl"];

    /// <summary>
    /// Reads and decodes one mesh file. Returns null having set <paramref name="error"/>.
    /// </summary>
    public static MeshData? Load(string path, bool close, bool smooth, out string? error)
    {
        byte[] bytes;

        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            error = e.Message;
            return null;
        }

        return Parse(path, bytes, close, smooth, out error);
    }

    /// <summary>
    /// Decodes bytes already in memory. <paramref name="name"/> is read for its extension only.
    /// </summary>
    public static MeshData? Parse(string name, byte[] bytes, bool close, bool smooth, out string? error)
    {
        string extension = Path.GetExtension(name).ToLowerInvariant();

        MeshData? raw = extension switch
        {
            ".obj" => ObjReader.Read(Text(bytes), out error),
            ".stl" => StlReader.Read(bytes, out error),
            _ => Unsupported(extension, out error),
        };

        return raw is null ? null : MeshTopology.Build(raw, close, smooth, out error);
    }

    /// <summary>
    /// What makes two meshes the same mesh, as a short hex string.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Load-bearing, and for a reason that is not obvious. Two roots are decided to be one shape
    /// by emitting each into a throwaway emitter and comparing the GLSL, and a leaf's shared body
    /// is deduplicated the same way. A mesh's geometry is not in its GLSL: the body carries a
    /// literal offset into the shape buffer and nothing else, and inside a throwaway emitter
    /// every mesh sits at offset zero. Without something to tell them apart, a teapot and a
    /// bunny in one scene would compare equal and one would be drawn as the other.
    /// </para>
    /// <para>
    /// So this goes into the leaf's key and into a comment in the emitted body, which is enough
    /// for both comparisons and costs nothing: the cost model does not count a comment line.
    /// </para>
    /// <para>
    /// Over the geometry rather than over the file's bytes, because the file is not what is
    /// uploaded: the same model welded, capped and smoothed differently is a different mesh, and
    /// two files that decode to the same triangles are the same one.
    /// </para>
    /// </remarks>
    public static string Signature(MeshData mesh, bool smooth)
    {
        using ContentSignature signature = new();

        signature.Add(mesh.Positions);
        signature.Add(mesh.Indices);
        signature.Add(smooth);

        return signature.ToString();
    }

    private static MeshData? Unsupported(string extension, out string? error)
    {
        string known = string.Join(" and ", Extensions.Select(e => $"'{e}'"));

        error = extension.Length == 0
            ? $"a mesh file needs an extension saying what it is; {known} are understood"
            : $"'{extension}' is not a mesh format this reads; {known} are understood";

        return null;
    }

    /// <summary>The bytes as text, with a byte order mark taken off if one is there.</summary>
    private static string Text(byte[] bytes) =>
        bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF
            ? Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3)
            : Encoding.UTF8.GetString(bytes);
}
