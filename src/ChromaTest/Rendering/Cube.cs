using Silk.NET.OpenGL;

namespace ChromaTest.Rendering;

/// <summary>
/// A unit cube centred on the origin, uploaded once to the GPU as a VAO/VBO/EBO trio.
/// Each face carries its own four vertices so it can hold a single flat normal.
/// </summary>
public sealed class Cube : IDisposable
{
    private const int FloatsPerVertex = 6; // position (3) + normal (3)
    private const uint IndexCount = 36;    // 6 faces * 2 triangles * 3 indices

    // Interleaved: px, py, pz, nx, ny, nz
    private static readonly float[] Vertices =
    {
        // Front (+Z)
        -0.5f, -0.5f,  0.5f,   0f,  0f,  1f,
         0.5f, -0.5f,  0.5f,   0f,  0f,  1f,
         0.5f,  0.5f,  0.5f,   0f,  0f,  1f,
        -0.5f,  0.5f,  0.5f,   0f,  0f,  1f,

        // Back (-Z)
         0.5f, -0.5f, -0.5f,   0f,  0f, -1f,
        -0.5f, -0.5f, -0.5f,   0f,  0f, -1f,
        -0.5f,  0.5f, -0.5f,   0f,  0f, -1f,
         0.5f,  0.5f, -0.5f,   0f,  0f, -1f,

        // Left (-X)
        -0.5f, -0.5f, -0.5f,  -1f,  0f,  0f,
        -0.5f, -0.5f,  0.5f,  -1f,  0f,  0f,
        -0.5f,  0.5f,  0.5f,  -1f,  0f,  0f,
        -0.5f,  0.5f, -0.5f,  -1f,  0f,  0f,

        // Right (+X)
         0.5f, -0.5f,  0.5f,   1f,  0f,  0f,
         0.5f, -0.5f, -0.5f,   1f,  0f,  0f,
         0.5f,  0.5f, -0.5f,   1f,  0f,  0f,
         0.5f,  0.5f,  0.5f,   1f,  0f,  0f,

        // Top (+Y)
        -0.5f,  0.5f,  0.5f,   0f,  1f,  0f,
         0.5f,  0.5f,  0.5f,   0f,  1f,  0f,
         0.5f,  0.5f, -0.5f,   0f,  1f,  0f,
        -0.5f,  0.5f, -0.5f,   0f,  1f,  0f,

        // Bottom (-Y)
        -0.5f, -0.5f, -0.5f,   0f, -1f,  0f,
         0.5f, -0.5f, -0.5f,   0f, -1f,  0f,
         0.5f, -0.5f,  0.5f,   0f, -1f,  0f,
        -0.5f, -0.5f,  0.5f,   0f, -1f,  0f,
    };

    // Two counter-clockwise triangles per face, in the order the vertices are declared.
    private static readonly uint[] Indices =
    {
         0,  1,  2,   2,  3,  0, // front
         4,  5,  6,   6,  7,  4, // back
         8,  9, 10,  10, 11,  8, // left
        12, 13, 14,  14, 15, 12, // right
        16, 17, 18,  18, 19, 16, // top
        20, 21, 22,  22, 23, 20, // bottom
    };

    private readonly GL _gl;
    private readonly uint _vao;
    private readonly uint _vbo;
    private readonly uint _ebo;

    public unsafe Cube(GL gl)
    {
        _gl = gl;

        _vao = _gl.GenVertexArray();
        _gl.BindVertexArray(_vao);

        _vbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        fixed (float* v = Vertices)
        {
            _gl.BufferData(
                BufferTargetARB.ArrayBuffer,
                (nuint)(Vertices.Length * sizeof(float)),
                v,
                BufferUsageARB.StaticDraw);
        }

        _ebo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);
        fixed (uint* i = Indices)
        {
            _gl.BufferData(
                BufferTargetARB.ElementArrayBuffer,
                (nuint)(Indices.Length * sizeof(uint)),
                i,
                BufferUsageARB.StaticDraw);
        }

        uint stride = FloatsPerVertex * sizeof(float);

        // location 0 -> aPosition
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
        _gl.EnableVertexAttribArray(0);

        // location 1 -> aNormal
        _gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof(float)));
        _gl.EnableVertexAttribArray(1);

        _gl.BindVertexArray(0);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);
        // The element buffer binding lives in the VAO, so it is left alone here.
    }

    public unsafe void Draw()
    {
        _gl.BindVertexArray(_vao);
        _gl.DrawElements(PrimitiveType.Triangles, IndexCount, DrawElementsType.UnsignedInt, (void*)0);
        _gl.BindVertexArray(0);
    }

    public void Dispose()
    {
        _gl.DeleteVertexArray(_vao);
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteBuffer(_ebo);
    }
}
