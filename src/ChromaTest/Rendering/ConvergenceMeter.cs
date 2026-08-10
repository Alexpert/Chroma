using Silk.NET.OpenGL;

namespace ChromaTest.Rendering;

/// <summary>
/// Measures how far the accumulated image still is from converged, as the mean relative
/// standard error of its lit pixels.
/// </summary>
/// <remarks>
/// <para>
/// A sample count alone cannot say whether an image is done: a flat diffuse scene is clean at
/// 50 samples where a caustic through glass is still boiling at 5000. The number produced here
/// is measured from the image itself, using the variance the trace already accumulates in the
/// alpha channel (see <see cref="AccumulationBuffer"/>).
/// </para>
/// <para>
/// Reducing a full frame to one number without compute shaders is the interesting part.
/// <c>convergence.frag</c> writes the per-pixel error, then <c>glGenerateMipmap</c> box-filters
/// the chain down to a single 1x1 texel that the driver has already averaged for us, and only
/// that one texel is read back. A full read of a 1280x720 float target would be 14 MB across
/// the bus every frame; this is 16 bytes.
/// </para>
/// </remarks>
public sealed class ConvergenceMeter : IDisposable
{
    /// <summary>Samples between two measurements.</summary>
    /// <remarks>
    /// The read-back below stalls the pipeline until the GPU catches up, and a figure
    /// flickering at 60 Hz is unreadable anyway. Four refreshes a second is plenty.
    /// </remarks>
    private const int MeasureInterval = 15;

    /// <summary>A variance needs at least two samples; one sample always reads as perfect.</summary>
    private const int MinimumSamples = 2;

    private readonly GL _gl;
    private uint _texture;
    private uint _framebuffer;
    private int _maxLevel;

    public ConvergenceMeter(GL gl, int width, int height)
    {
        _gl = gl;
        Create(width, height);
    }

    /// <summary>
    /// Mean relative standard error over the lit pixels, as a fraction: 0.018 is 1.8% noise.
    /// <see cref="float.NaN"/> until the first measurement, which needs two samples.
    /// </summary>
    public float RelativeError { get; private set; } = float.NaN;

    /// <summary>
    /// Re-measures if enough samples have arrived since the last time; cheap to call every
    /// frame.
    /// </summary>
    /// <param name="shader">The program built from <c>convergence.frag</c>.</param>
    /// <param name="quad">The same fullscreen quad the render passes use.</param>
    /// <param name="accumulationTexture">
    /// <see cref="AccumulationBuffer.ResultTexture"/>: RGB mean radiance, alpha mean squared
    /// luminance.
    /// </param>
    /// <param name="sampleCount">Samples per pixel that texture holds.</param>
    public unsafe void Update(Shader shader, FullscreenQuad quad, uint accumulationTexture, int sampleCount)
    {
        if (sampleCount < MinimumSamples || sampleCount % MeasureInterval != 0)
        {
            return;
        }

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _framebuffer);

        shader.Use();
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, accumulationTexture);
        shader.SetUniform("uAccumulation", 0);
        shader.SetUniform("uSampleCount", sampleCount);

        quad.Draw();

        // The reduction. Box filtering a non-power-of-two image weights the edge texels
        // slightly differently from the interior, which would matter for a filtered lookup
        // and does not for a noise readout.
        _gl.BindTexture(TextureTarget.Texture2D, _texture);
        _gl.GenerateMipmap(TextureTarget.Texture2D);

        Span<float> reduced = stackalloc float[4];
        fixed (float* pixels = reduced)
        {
            _gl.GetTexImage(TextureTarget.Texture2D, _maxLevel, PixelFormat.Rgba, PixelType.Float, pixels);
        }

        // Red is the summed error, green the number of pixels that counted, both divided by
        // the same texel count by the mipmap chain -- so their ratio is the mean over the lit
        // pixels alone. An entirely black frame leaves green at zero and nothing to report.
        float weight = reduced[1];
        RelativeError = weight > 0f ? reduced[0] / weight : float.NaN;

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    public void Resize(int width, int height)
    {
        Destroy();
        Create(width, height);
        RelativeError = float.NaN;
    }

    public void Dispose() => Destroy();

    private unsafe void Create(int width, int height)
    {
        uint w = (uint)Math.Max(width, 1);
        uint h = (uint)Math.Max(height, 1);

        // Half floats, not full: RGBA16F is guaranteed both colour-renderable and *filterable*
        // in OpenGL 3.3 core, and filterable is what glGenerateMipmap needs. Eleven bits of
        // mantissa is far more than a percentage displayed to two decimals asks for.
        _texture = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, _texture);
        _gl.TexImage2D(
            TextureTarget.Texture2D,
            0,
            InternalFormat.Rgba16f,
            w,
            h,
            0,
            PixelFormat.Rgba,
            PixelType.Float,
            null);

        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.LinearMipmapLinear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);

        _framebuffer = _gl.GenFramebuffer();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _framebuffer);
        _gl.FramebufferTexture2D(
            FramebufferTarget.Framebuffer,
            FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D,
            _texture,
            0);

        GLEnum status = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (status != GLEnum.FramebufferComplete)
        {
            throw new InvalidOperationException($"Convergence framebuffer is incomplete: {status}.");
        }

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        _gl.BindTexture(TextureTarget.Texture2D, 0);

        // Each level halves both dimensions, rounding down, so the chain ends at 1x1 after
        // log2 of the longer side.
        _maxLevel = (int)Math.Floor(Math.Log2(Math.Max(w, h)));
    }

    private void Destroy()
    {
        _gl.DeleteFramebuffer(_framebuffer);
        _gl.DeleteTexture(_texture);
    }
}
