using Silk.NET.OpenGL;

namespace ChromaTest.Rendering;

/// <summary>
/// The one piece of geometry a ray tracer needs: a quad covering the whole viewport, so
/// the fragment shader runs once per pixel.
/// </summary>
/// <remarks>
/// Positions are already in clip space, so there is no model, view or projection matrix
/// anywhere — the camera lives entirely in the ray generation code. Four vertices drawn as
/// a triangle strip need no index buffer.
/// </remarks>
public sealed class FullscreenQuad : IDisposable
{
    private const int FloatsPerVertex = 2;
    private const uint VertexCount = 4;

    // Clip-space corners, in triangle-strip order: bottom-left, bottom-right, top-left,
    // top-right.
    private static readonly float[] Vertices =
    {
        -1f, -1f,
         1f, -1f,
        -1f,  1f,
         1f,  1f,
    };

    private readonly GL _gl;
    private readonly uint _vao;
    private readonly uint _vbo;

    public unsafe FullscreenQuad(GL gl)
    {
        _gl = gl;

        _vao = _gl.GenVertexArray();
        _gl.BindVertexArray(_vao);

        _vbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);

        fixed (float* vertices = Vertices)
        {
            _gl.BufferData(
                BufferTargetARB.ArrayBuffer,
                (nuint)(Vertices.Length * sizeof(float)),
                vertices,
                BufferUsageARB.StaticDraw);
        }

        // Attribute 0 matches `layout (location = 0) in vec2 aPosition` in raytrace.vert.
        _gl.VertexAttribPointer(
            0,
            FloatsPerVertex,
            VertexAttribPointerType.Float,
            false,
            FloatsPerVertex * sizeof(float),
            (void*)0);

        _gl.EnableVertexAttribArray(0);

        _gl.BindVertexArray(0);
    }

    public void Draw()
    {
        _gl.BindVertexArray(_vao);
        _gl.DrawArrays(PrimitiveType.TriangleStrip, 0, VertexCount);
        _gl.BindVertexArray(0);
    }

    public void Dispose()
    {
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteVertexArray(_vao);
    }
}
