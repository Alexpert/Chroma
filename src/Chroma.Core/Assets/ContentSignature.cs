using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;

namespace Chroma.Core.Assets;

/// <summary>
/// A short hash over what a shape <b>is</b>, for the two primitives whose geometry does not go
/// into the source they emit.
/// </summary>
/// <remarks>
/// <para>
/// Two roots are decided to be one shape by emitting each into a throwaway
/// <c>GeometryEmitter</c> and comparing the GLSL text, which works because every other primitive
/// writes its geometry out as literals: what a shape is, is what it emits. A mesh and a height
/// field break that. Their data lives in the shape buffer and their bodies carry one literal
/// offset into it, and inside a probe every buffer starts empty, so every one of them sits at
/// offset zero and any two emit identical text. Two different models in one scene would compare
/// equal and the second would be drawn as the first, with nothing to say so.
/// </para>
/// <para>
/// So each of them hashes its own geometry, carries the result on its <c>LeafPlan</c>, and writes
/// it into the emitted body as a comment. A comment costs nothing, because
/// <c>GlslWriter</c> does not count a line starting with <c>//</c>, so the cost model is
/// untouched and both comparisons become correct again.
/// </para>
/// <para>
/// Sixteen hex characters of SHA-256. Not a cryptographic requirement: it is a name for a
/// content, and sixty-four bits is far past what a scene can collide in.
/// </para>
/// </remarks>
public sealed class ContentSignature : IDisposable
{
    // One scratch buffer written into and appended from, rather than a byte array per value.
    // A bunny is 337,206 numbers and a height field at the cap is a million more.
    private const int Chunk = 4096;

    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly byte[] _buffer = new byte[Chunk];

    private int _at;

    /// <summary>A float, by its bit pattern, so that two equal values hash the same.</summary>
    public void Add(float value) => Add(BitConverter.SingleToInt32Bits(value));

    public void Add(Vector3 value)
    {
        Add(value.X);
        Add(value.Y);
        Add(value.Z);
    }

    public void Add(int value)
    {
        if (_at + 4 > Chunk)
        {
            Flush();
        }

        BinaryPrimitives.WriteInt32LittleEndian(_buffer.AsSpan(_at), value);
        _at += 4;
    }

    /// <summary>A flag, in one byte, because a flag changes what is uploaded.</summary>
    public void Add(bool value)
    {
        if (_at + 1 > Chunk)
        {
            Flush();
        }

        _buffer[_at] = value ? (byte)1 : (byte)0;
        _at++;
    }

    public void Add(IReadOnlyList<float> values)
    {
        foreach (float value in values)
        {
            Add(value);
        }
    }

    public void Add(IReadOnlyList<int> values)
    {
        foreach (int value in values)
        {
            Add(value);
        }
    }

    public void Add(IReadOnlyList<Vector3> values)
    {
        foreach (Vector3 value in values)
        {
            Add(value);
        }
    }

    /// <summary>The signature, which also resets this instance.</summary>
    public override string ToString()
    {
        Flush();

        return Convert.ToHexString(_hash.GetHashAndReset())[..16].ToLowerInvariant();
    }

    public void Dispose() => _hash.Dispose();

    private void Flush()
    {
        if (_at == 0)
        {
            return;
        }

        _hash.AppendData(_buffer, 0, _at);
        _at = 0;
    }
}
