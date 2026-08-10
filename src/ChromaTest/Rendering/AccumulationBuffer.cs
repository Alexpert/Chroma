using Silk.NET.OpenGL;

namespace ChromaTest.Rendering;

/// <summary>
/// The progressive image: two floating-point render targets, ping-ponged, holding the
/// running average of every sample taken so far.
/// </summary>
/// <remarks>
/// <para>
/// One sample per pixel per frame is nowhere near enough for a path tracer, so the samples
/// are averaged across frames instead of looping inside the shader. The window stays
/// responsive and the image improves while it is watched.
/// </para>
/// <para>
/// Ping-pong rather than additive blending into one target: blending is less code, but
/// 32-bit float blending is exactly the kind of support that varies between drivers, while
/// reading one texture and writing another depends on nothing. Float textures and
/// framebuffer objects are both core in OpenGL 3.3, so this costs no version bump.
/// </para>
/// <para>
/// RGB holds the running mean radiance and alpha the running mean of the squared luminance,
/// so a reader has everything needed for the per-pixel variance without a second buffer —
/// see <c>convergence.frag</c>. Nothing in the display path depends on alpha: the resolve
/// pass reads <c>.rgb</c> only.
/// </para>
/// <para>
/// The order within a frame is fixed, and the two texture accessors are named for the roles
/// they hold during it:
/// </para>
/// <code>
/// 1. trace into WriteFramebuffer, sampling HistoryTexture
/// 2. resolve to the screen, sampling ResultTexture
/// 3. Advance()
/// </code>
/// </remarks>
public sealed class AccumulationBuffer : IDisposable
{
    private readonly GL _gl;
    private Target _front;
    private Target _back;

    public AccumulationBuffer(GL gl, int width, int height)
    {
        _gl = gl;
        _front = Target.Create(gl, width, height);
        _back = Target.Create(gl, width, height);
        Reset();
    }

    /// <summary>How many samples per pixel the history already holds.</summary>
    public int SampleIndex { get; private set; }

    /// <summary>Where this frame's trace writes.</summary>
    public uint WriteFramebuffer => _back.Framebuffer;

    /// <summary>What the trace reads: everything accumulated before this frame.</summary>
    public uint HistoryTexture => _front.Texture;

    /// <summary>What the resolve pass reads: the average including this frame.</summary>
    public uint ResultTexture => _back.Texture;

    public void Resize(int width, int height)
    {
        _front.Dispose(_gl);
        _back.Dispose(_gl);
        _front = Target.Create(_gl, width, height);
        _back = Target.Create(_gl, width, height);
        Reset();
    }

    /// <summary>
    /// Throws away every sample taken so far.
    /// </summary>
    /// <remarks>
    /// Required whenever a sample stops meaning what earlier samples meant — a resize, or
    /// any change to the camera or the scene. Skipping it leaves a ghost of the previous
    /// state fading slowly instead of disappearing.
    /// </remarks>
    public void Reset()
    {
        // Alpha clears to zero like the colour: it holds mean squared luminance, not opacity.
        _gl.ClearColor(0f, 0f, 0f, 0f);
        Clear(_front);
        Clear(_back);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);

        SampleIndex = 0;
    }

    /// <summary>Makes this frame's result the next frame's history.</summary>
    public void Advance()
    {
        (_front, _back) = (_back, _front);
        SampleIndex++;
    }

    public void Dispose()
    {
        _front.Dispose(_gl);
        _back.Dispose(_gl);
    }

    private void Clear(Target target)
    {
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, target.Framebuffer);
        _gl.Clear(ClearBufferMask.ColorBufferBit);
    }

    private readonly record struct Target(uint Texture, uint Framebuffer)
    {
        public static unsafe Target Create(GL gl, int width, int height)
        {
            uint texture = gl.GenTexture();
            gl.BindTexture(TextureTarget.Texture2D, texture);

            gl.TexImage2D(
                TextureTarget.Texture2D,
                0,
                InternalFormat.Rgba32f,
                (uint)Math.Max(width, 1),
                (uint)Math.Max(height, 1),
                0,
                PixelFormat.Rgba,
                PixelType.Float,
                null);

            // Nearest and clamped: this is a data buffer read one texel per pixel, not an
            // image being sampled. Leaving the default mipmap filter makes the texture
            // incomplete and it reads as black, which looks exactly like a broken shader.
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);

            uint framebuffer = gl.GenFramebuffer();
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
            gl.FramebufferTexture2D(
                FramebufferTarget.Framebuffer,
                FramebufferAttachment.ColorAttachment0,
                TextureTarget.Texture2D,
                texture,
                0);

            GLEnum status = gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
            if (status != GLEnum.FramebufferComplete)
            {
                // A programmer error, like a shader that will not compile: fail with the
                // driver's own answer rather than render a black window.
                throw new InvalidOperationException(
                    $"Accumulation framebuffer is incomplete: {status}.");
            }

            gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            gl.BindTexture(TextureTarget.Texture2D, 0);

            return new Target(texture, framebuffer);
        }

        public void Dispose(GL gl)
        {
            gl.DeleteFramebuffer(Framebuffer);
            gl.DeleteTexture(Texture);
        }
    }
}
