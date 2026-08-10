using System.Buffers.Binary;
using System.IO.Compression;

namespace Chroma.Rendering;

/// <summary>
/// A minimal PNG encoder: 8-bit RGB, no interlacing, no ancillary chunks.
/// </summary>
/// <remarks>
/// <para>
/// .NET has no cross-platform image encoder in the box, and every package that provides one
/// costs more than this file does. PNG is only three chunks around a zlib stream, and
/// <see cref="ZLibStream"/> — which is in the framework since .NET 6 — writes exactly the zlib
/// header and Adler-32 trailer the format asks for, leaving nothing here but the chunk
/// framing and a CRC.
/// </para>
/// <para>
/// Every scanline goes out with filter type 0 (none). The filters exist to make the image more
/// compressible; leaving them off costs some file size on a photographic image and no
/// correctness at all.
/// </para>
/// </remarks>
internal static class PngWriter
{
    private const int BytesPerPixel = 3;
    private const byte BitDepth = 8;
    private const byte ColourTypeTruecolour = 2;
    private const byte FilterNone = 0;

    private static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };

    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <summary>Writes <paramref name="rgb"/> as a PNG, top row first.</summary>
    /// <param name="rgb">
    /// <paramref name="width"/> * <paramref name="height"/> * 3 bytes, row-major, no padding.
    /// </param>
    public static void Write(string path, ReadOnlySpan<byte> rgb, int width, int height)
    {
        int expected = width * height * BytesPerPixel;
        if (rgb.Length < expected)
        {
            throw new ArgumentException(
                $"Expected {expected} bytes for {width}x{height} RGB, got {rgb.Length}.", nameof(rgb));
        }

        byte[] header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)height);
        header[8] = BitDepth;
        header[9] = ColourTypeTruecolour;
        // Compression 0 (deflate), filter 0 (adaptive), interlace 0 (none) -- the only values
        // the specification defines for any of the three.

        using var file = new FileStream(path, FileMode.Create, FileAccess.Write);
        file.Write(Signature);

        WriteChunk(file, "IHDR", header);
        WriteChunk(file, "IDAT", Compress(rgb, width, height));
        WriteChunk(file, "IEND", ReadOnlySpan<byte>.Empty);
    }

    private static byte[] Compress(ReadOnlySpan<byte> rgb, int width, int height)
    {
        int stride = width * BytesPerPixel;

        using var compressed = new MemoryStream(stride * height / 2);
        using (var deflate = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            for (int y = 0; y < height; y++)
            {
                deflate.WriteByte(FilterNone);
                deflate.Write(rgb.Slice(y * stride, stride));
            }
        }

        return compressed.ToArray();
    }

    private static void WriteChunk(Stream output, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        output.Write(length);

        // Chunk types are ASCII by definition, and the CRC covers the type as well as the
        // payload -- so it is written through the same buffer the CRC reads.
        Span<byte> typeBytes = stackalloc byte[4];
        for (int i = 0; i < 4; i++)
        {
            typeBytes[i] = (byte)type[i];
        }

        output.Write(typeBytes);
        output.Write(data);

        uint crc = Crc(typeBytes, data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        output.Write(crcBytes);
    }

    private static uint Crc(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        crc = Accumulate(crc, type);
        crc = Accumulate(crc, data);
        return crc ^ 0xFFFFFFFFu;
    }

    private static uint Accumulate(uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (byte b in bytes)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        // The CRC-32 of the PNG specification: the standard reflected polynomial 0xEDB88320.
        uint[] table = new uint[256];

        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}
