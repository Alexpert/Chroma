using ChromaTest.Core.Compilation;
using Silk.NET.OpenGL;

namespace ChromaTest.Rendering;

/// <summary>
/// The compiled scene, resident on the GPU as four texture buffers.
/// </summary>
/// <remarks>
/// <para>
/// OpenGL 3.3 has no shader storage buffers — those arrived in 4.3 — so the scene travels
/// as texture buffer objects, core since GL 3.1: a plain buffer viewed through a texture,
/// read with <c>texelFetch</c> by integer index, one <c>vec4</c> or <c>ivec4</c> per texel,
/// no filtering and no practical size limit at this scale.
/// </para>
/// <para>
/// Each of the four is a buffer object plus a texture that views it. Both handles have to
/// be kept: deleting the buffer while the texture still refers to it is what produces a
/// scene that renders as noise.
/// </para>
/// </remarks>
public sealed class SceneBuffers : IDisposable
{
    /// <summary>Texture units, matching the sampler uniforms set in <see cref="BindTo"/>.</summary>
    private const int PrimitivesUnit = 0;
    private const int TapeUnit = 1;
    private const int MaterialsUnit = 2;
    private const int ShapesUnit = 3;

    private readonly GL _gl;
    private readonly TextureBuffer _primitives;
    private readonly TextureBuffer _tape;
    private readonly TextureBuffer _materials;
    private readonly TextureBuffer _shapes;

    public SceneBuffers(GL gl, CompiledScene scene)
    {
        _gl = gl;

        _primitives = TextureBuffer.Create<float>(gl, scene.Primitives, SizedInternalFormat.Rgba32f);
        _tape = TextureBuffer.Create<int>(gl, scene.Tape, SizedInternalFormat.Rgba32i);
        _materials = TextureBuffer.Create<float>(gl, scene.Materials, SizedInternalFormat.Rgba32f);

        // Usually empty — only a prism, a lathe or a blob puts anything here. Create() pads
        // an empty buffer to one texel, so the sampler is always bound to something real.
        _shapes = TextureBuffer.Create<float>(gl, scene.Shapes, SizedInternalFormat.Rgba32f);
    }

    /// <summary>Binds the four buffers and tells the shader which unit each is on.</summary>
    public void BindTo(Shader shader)
    {
        Bind(TextureUnit.Texture0, _primitives);
        Bind(TextureUnit.Texture1, _tape);
        Bind(TextureUnit.Texture2, _materials);
        Bind(TextureUnit.Texture3, _shapes);

        shader.SetUniform("uPrimitives", PrimitivesUnit);
        shader.SetUniform("uTape", TapeUnit);
        shader.SetUniform("uMaterials", MaterialsUnit);
        shader.SetUniform("uShapes", ShapesUnit);
    }

    public void Dispose()
    {
        _shapes.Dispose(_gl);
        _materials.Dispose(_gl);
        _tape.Dispose(_gl);
        _primitives.Dispose(_gl);
    }

    private void Bind(TextureUnit unit, TextureBuffer buffer)
    {
        _gl.ActiveTexture(unit);
        _gl.BindTexture(TextureTarget.TextureBuffer, buffer.Texture);
    }

    private readonly record struct TextureBuffer(uint Buffer, uint Texture)
    {
        public static unsafe TextureBuffer Create<T>(
            GL gl,
            ReadOnlySpan<T> data,
            SizedInternalFormat format)
            where T : unmanaged
        {
            uint buffer = gl.GenBuffer();
            gl.BindBuffer(BufferTargetARB.TextureBuffer, buffer);

            // A zero-length buffer is legal but leaves drivers free to disagree about what
            // a sampler bound to it reads. One empty texel costs nothing and removes the
            // question; the shader never fetches it, because the tape length gates access.
            if (data.IsEmpty)
            {
                Span<T> placeholder = stackalloc T[4];
                Upload<T>(gl, placeholder);
            }
            else
            {
                Upload(gl, data);
            }

            uint texture = gl.GenTexture();
            gl.BindTexture(TextureTarget.TextureBuffer, texture);
            gl.TexBuffer(TextureTarget.TextureBuffer, format, buffer);

            gl.BindTexture(TextureTarget.TextureBuffer, 0);
            gl.BindBuffer(BufferTargetARB.TextureBuffer, 0);

            return new TextureBuffer(buffer, texture);
        }

        public void Dispose(GL gl)
        {
            gl.DeleteTexture(Texture);
            gl.DeleteBuffer(Buffer);
        }

        private static unsafe void Upload<T>(GL gl, ReadOnlySpan<T> data)
            where T : unmanaged
        {
            fixed (T* pointer = data)
            {
                gl.BufferData(
                    BufferTargetARB.TextureBuffer,
                    (nuint)(data.Length * sizeof(T)),
                    pointer,
                    BufferUsageARB.StaticDraw);
            }
        }
    }
}
