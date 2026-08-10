using Silk.NET.OpenGL;

namespace Chroma.Rendering;

/// <summary>
/// Saves what is currently on screen to a PNG.
/// </summary>
/// <remarks>
/// The window is read rather than the accumulation buffer, so the file matches the image the
/// exposure and tone-mapping decisions were made against, pixel for pixel. The linear HDR
/// buffer holds more information, but nothing in this project can display it.
/// </remarks>
internal static class ImageCapture
{
    private const int BytesPerPixel = 3;

    /// <summary>Reads the back buffer and writes it to <paramref name="path"/>.</summary>
    /// <remarks>
    /// Must be called from inside the render callback, after the resolve pass and before
    /// anything else draws: the back buffer holds the finished frame until the windowing
    /// layer presents it, and whatever is drawn on top would be captured with it.
    /// </remarks>
    public static unsafe void SaveWindow(GL gl, int width, int height, string path)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentException("The window has no pixels to capture.");
        }

        gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        gl.ReadBuffer(ReadBufferMode.Back);

        // Rows of an odd width are not a multiple of four bytes, and the default pack
        // alignment would have the driver skip to the next boundary between them -- which
        // shears the image diagonally rather than failing.
        gl.PixelStore(PixelStoreParameter.PackAlignment, 1);

        int stride = width * BytesPerPixel;
        byte[] pixels = new byte[stride * height];

        fixed (byte* destination = pixels)
        {
            gl.ReadPixels(0, 0, (uint)width, (uint)height, PixelFormat.Rgb, PixelType.UnsignedByte, destination);
        }

        FlipVertically(pixels, stride, height);

        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        PngWriter.Write(path, pixels, width, height);
    }

    /// <summary>OpenGL's first row is the bottom of the image; a PNG's is the top.</summary>
    private static void FlipVertically(byte[] pixels, int stride, int height)
    {
        byte[] row = new byte[stride];

        for (int y = 0; y < height / 2; y++)
        {
            int top = y * stride;
            int bottom = (height - 1 - y) * stride;

            Array.Copy(pixels, top, row, 0, stride);
            Array.Copy(pixels, bottom, pixels, top, stride);
            Array.Copy(row, 0, pixels, bottom, stride);
        }
    }
}
