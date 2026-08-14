using Chroma.Core.Compilation;
using Silk.NET.OpenGL;

namespace Chroma.Rendering;

/// <summary>
/// The compiled scene, resident on the GPU as buffers.
/// </summary>
/// <remarks>
/// <para>
/// The buffers hold the same bytes on both tiers and the shader indexes them the same way; only
/// how they are bound differs. On the compute tier they are <b>shader storage buffers</b>, bound
/// to explicit binding points — the direct thing, available since OpenGL 4.3. On the OpenGL 3.3
/// fallback there are no storage buffers, so they are <b>texture buffer objects</b> instead: a
/// plain buffer viewed through a texture, read with <c>texelFetch</c> by integer index, core since
/// GL 3.1. See the PRIMITIVE/MATERIAL/SHAPE macros in raytrace.glsl.
/// </para>
/// <para>
/// A texture buffer is a buffer object plus a texture that views it, and both handles have to be
/// kept: deleting the buffer while the texture still refers to it is what produces a scene that
/// renders as noise.
/// </para>
/// <para>
/// There was a fourth buffer, holding the instruction tape, until the scene stopped being data an
/// interpreter walks. Three were left, and all three were read by the shading path only: which
/// leaf was hit, what it is made of, and, for the four primitives defined by a list, the points
/// its normal is built from.
/// </para>
/// <para>
/// Instancing adds two the <b>span</b> path reads, and they are the first thing about a scene's
/// shape to live in memory since the tape went: where each appearance of a repeated shape stands,
/// and the tree that finds the few a ray could meet. They exist only for a scene that repeats
/// something, which is also the only kind of scene whose shader declares them.
/// </para>
/// </remarks>
public sealed class SceneBuffers : IDisposable
{
    /// <summary>Texture units on the fallback path, matching the samplers set in <see cref="BindTo"/>.</summary>
    private const int PrimitivesUnit = 0;
    private const int MaterialsUnit = 1;
    private const int ShapesUnit = 2;
    private const int InstancesUnit = 3;
    private const int NodesUnit = 4;

    /// <summary>
    /// Storage-buffer binding points on the compute path. Declared in the shader with
    /// <c>layout(std430, binding = N)</c>, so unlike a sampler uniform there is no name to look
    /// up and nothing for the driver to strip.
    /// </summary>
    private const uint PrimitivesBinding = 0;
    private const uint MaterialsBinding = 1;
    private const uint ShapesBinding = 2;
    private const uint InstancesBinding = 3;
    private const uint NodesBinding = 4;

    private readonly GL _gl;
    private readonly bool _storage;
    private readonly bool _instanced;
    private readonly SceneBuffer _primitives;
    private readonly SceneBuffer _materials;
    private readonly SceneBuffer _shapes;
    private readonly SceneBuffer _instances;
    private readonly SceneBuffer _nodes;

    public SceneBuffers(GL gl, CompiledScene scene, bool storage)
    {
        _gl = gl;
        _storage = storage;

        // A scene that repeats nothing has no instance table and no shader that reads one, so
        // there is nothing to create and nothing to bind. The shader is compiled without the
        // declarations too (see CHROMA_INSTANCES in raytrace.glsl), and the two conditions are
        // read off the same number so they cannot disagree.
        _instanced = scene.InstanceCount > 0;

        _primitives = SceneBuffer.Create<float>(gl, scene.Primitives, storage);
        _materials = SceneBuffer.Create<float>(gl, scene.Materials, storage);

        // Usually empty — only a prism, a lathe, a blob or a sweep puts anything here. Create()
        // pads an empty buffer to one texel, so what the shader reads is always something real.
        _shapes = SceneBuffer.Create<float>(gl, scene.Shapes, storage);

        if (_instanced)
        {
            _instances = SceneBuffer.Create<float>(gl, scene.Instances, storage);
            _nodes = SceneBuffer.Create<float>(gl, scene.Nodes, storage);
        }
    }

    /// <summary>Makes the three buffers readable by the given program.</summary>
    public void BindTo(Shader shader)
    {
        if (_storage)
        {
            _gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, PrimitivesBinding, _primitives.Buffer);
            _gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, MaterialsBinding, _materials.Buffer);
            _gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, ShapesBinding, _shapes.Buffer);

            if (_instanced)
            {
                _gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, InstancesBinding, _instances.Buffer);
                _gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, NodesBinding, _nodes.Buffer);
            }

            return;
        }

        Bind(TextureUnit.Texture0, _primitives);
        Bind(TextureUnit.Texture1, _materials);
        Bind(TextureUnit.Texture2, _shapes);

        shader.SetUniform("uPrimitives", PrimitivesUnit);
        shader.SetUniform("uMaterials", MaterialsUnit);
        shader.SetUniform("uShapes", ShapesUnit);

        if (!_instanced)
        {
            return;
        }

        Bind(TextureUnit.Texture3, _instances);
        Bind(TextureUnit.Texture4, _nodes);

        shader.SetUniform("uInstances", InstancesUnit);
        shader.SetUniform("uNodes", NodesUnit);
    }

    public void Dispose()
    {
        if (_instanced)
        {
            _nodes.Dispose(_gl);
            _instances.Dispose(_gl);
        }

        _shapes.Dispose(_gl);
        _materials.Dispose(_gl);
        _primitives.Dispose(_gl);
    }

    private void Bind(TextureUnit unit, SceneBuffer buffer)
    {
        _gl.ActiveTexture(unit);
        _gl.BindTexture(TextureTarget.TextureBuffer, buffer.Texture);
    }

    /// <param name="Texture">The view onto the buffer, or 0 when nothing needs to view it.</param>
    private readonly record struct SceneBuffer(uint Buffer, uint Texture)
    {
        public static unsafe SceneBuffer Create<T>(GL gl, ReadOnlySpan<T> data, bool storage)
            where T : unmanaged
        {
            // Which target the data is uploaded through does not matter -- a buffer object holds
            // bytes and the target is a binding slot, not a type -- but using the one it will be
            // read through keeps the driver's hint honest.
            BufferTargetARB target = storage
                ? BufferTargetARB.ShaderStorageBuffer
                : BufferTargetARB.TextureBuffer;

            uint buffer = gl.GenBuffer();
            gl.BindBuffer(target, buffer);

            // A zero-length buffer is legal but leaves drivers free to disagree about what a read
            // from it returns. One empty texel costs nothing and removes the question; the shader
            // never reads it, because a primitive that has no shape data never asks for any.
            if (data.IsEmpty)
            {
                Span<T> placeholder = stackalloc T[4];
                Upload<T>(gl, target, placeholder);
            }
            else
            {
                Upload(gl, target, data);
            }

            gl.BindBuffer(target, 0);

            if (storage)
            {
                return new SceneBuffer(buffer, 0);
            }

            uint texture = gl.GenTexture();
            gl.BindTexture(TextureTarget.TextureBuffer, texture);
            gl.TexBuffer(TextureTarget.TextureBuffer, SizedInternalFormat.Rgba32f, buffer);
            gl.BindTexture(TextureTarget.TextureBuffer, 0);

            return new SceneBuffer(buffer, texture);
        }

        public void Dispose(GL gl)
        {
            if (Texture != 0)
            {
                gl.DeleteTexture(Texture);
            }

            gl.DeleteBuffer(Buffer);
        }

        private static unsafe void Upload<T>(GL gl, BufferTargetARB target, ReadOnlySpan<T> data)
            where T : unmanaged
        {
            fixed (T* pointer = data)
            {
                gl.BufferData(
                    target,
                    (nuint)(data.Length * sizeof(T)),
                    pointer,
                    BufferUsageARB.StaticDraw);
            }
        }
    }
}
