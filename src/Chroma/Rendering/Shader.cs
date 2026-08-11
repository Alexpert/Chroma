using System.Numerics;
using Silk.NET.OpenGL;

namespace Chroma.Rendering;

/// <summary>
/// A linked vertex + fragment shader program loaded from two files on disk.
/// Compilation and link errors are surfaced as exceptions carrying the driver's
/// info log verbatim, which is what makes editing the .vert/.frag files practical.
/// </summary>
public sealed class Shader : IDisposable
{
    private readonly GL _gl;
    private readonly uint _handle;
    private readonly Dictionary<string, int> _uniformLocations = new();

    /// <param name="defines">
    /// Preprocessor symbols to compile the fragment stage with, as <c>NAME=value</c>.
    /// </param>
    /// <remarks>
    /// <para>
    /// Specialising the shader to the scene rather than branching on a uniform. The two look
    /// equivalent — a uniform is constant across the whole draw, and any sane compiler folds a
    /// branch on it — and on this renderer they are not: what a driver charges for is not
    /// executing a branch but <b>compiling</b> it. Code that no ray in a scene ever reaches
    /// still occupies registers in the schedule around it, and this shader is close enough to
    /// the occupancy cliff that the difference is a factor of two. Iteration 11 measured a
    /// branch fog.chroma never executed costing that scene 2.3x.
    /// </para>
    /// <para>
    /// Legitimate here because a scene cannot change without a reload: the symbols are read off
    /// the compiled scene once, and nothing can invalidate them while the program lives.
    /// </para>
    /// </remarks>
    public Shader(GL gl, string vertexPath, string fragmentPath, IReadOnlyList<string>? defines = null)
    {
        _gl = gl;

        uint vertex = CompileStage(ShaderType.VertexShader, vertexPath);
        uint fragment = CompileStage(ShaderType.FragmentShader, fragmentPath, defines);

        _handle = _gl.CreateProgram();
        _gl.AttachShader(_handle, vertex);
        _gl.AttachShader(_handle, fragment);
        _gl.LinkProgram(_handle);

        _gl.GetProgram(_handle, ProgramPropertyARB.LinkStatus, out int linked);
        if (linked == 0)
        {
            string log = _gl.GetProgramInfoLog(_handle);
            _gl.DeleteProgram(_handle);
            _gl.DeleteShader(vertex);
            _gl.DeleteShader(fragment);
            throw new InvalidOperationException($"Failed to link shader program:\n{log}");
        }

        // The stages are baked into the program at this point.
        _gl.DetachShader(_handle, vertex);
        _gl.DetachShader(_handle, fragment);
        _gl.DeleteShader(vertex);
        _gl.DeleteShader(fragment);
    }

    public void Use() => _gl.UseProgram(_handle);

    public void SetUniform(string name, Matrix4x4 value)
    {
        int location = GetUniformLocation(name);

        // System.Numerics stores a Matrix4x4 row-major and uses row-vector math (v * M),
        // while GLSL reads memory column-major and uses column-vector math (M * v).
        // Uploading untransposed makes GLSL see the transpose, which is exactly what
        // the column-vector convention needs -- so transpose stays false and the
        // shaders write `uProjection * uView * uModel * vec4(pos, 1.0)`.
        unsafe
        {
            _gl.UniformMatrix4(location, 1, false, (float*)&value);
        }
    }

    public void SetUniform(string name, int value) =>
        _gl.Uniform1(GetUniformLocation(name), value);

    public void SetUniform(string name, float value) =>
        _gl.Uniform1(GetUniformLocation(name), value);

    public void SetUniform(string name, Vector2 value) =>
        _gl.Uniform2(GetUniformLocation(name), value.X, value.Y);

    public void SetUniform(string name, Vector3 value) =>
        _gl.Uniform3(GetUniformLocation(name), value.X, value.Y, value.Z);

    /// <summary>
    /// Uploads the first <paramref name="count"/> elements of an <c>int[]</c> uniform
    /// array. The location of element zero addresses the whole array, so only that one
    /// name is looked up.
    /// </summary>
    public void SetUniform(string name, ReadOnlySpan<int> values, int count)
    {
        if (count == 0)
        {
            return;
        }

        unsafe
        {
            fixed (int* data = values)
            {
                _gl.Uniform1(GetUniformLocation($"{name}[0]"), (uint)count, data);
            }
        }
    }

    /// <summary>Uploads the first <paramref name="count"/> elements of a <c>float[]</c>.</summary>
    public void SetUniform(string name, ReadOnlySpan<float> values, int count)
    {
        if (count == 0)
        {
            return;
        }

        unsafe
        {
            fixed (float* data = values)
            {
                _gl.Uniform1(GetUniformLocation($"{name}[0]"), (uint)count, data);
            }
        }
    }

    /// <summary>Uploads the first <paramref name="count"/> elements of a <c>vec3[]</c>.</summary>
    public void SetUniform(string name, ReadOnlySpan<Vector3> values, int count)
    {
        if (count == 0)
        {
            return;
        }

        unsafe
        {
            fixed (Vector3* data = values)
            {
                _gl.Uniform3(GetUniformLocation($"{name}[0]"), (uint)count, (float*)data);
            }
        }
    }

    private int GetUniformLocation(string name)
    {
        if (_uniformLocations.TryGetValue(name, out int cached))
        {
            return cached;
        }

        int location = _gl.GetUniformLocation(_handle, name);
        if (location == -1)
        {
            // Either a typo, or the uniform was optimised out because the shader never
            // reads it. Fail loudly rather than silently drawing nothing.
            throw new InvalidOperationException(
                $"Uniform '{name}' not found in the shader program (unused uniforms are stripped by the driver).");
        }

        _uniformLocations[name] = location;
        return location;
    }

    private uint CompileStage(ShaderType type, string path, IReadOnlyList<string>? defines = null)
    {
        string source = Inject(File.ReadAllText(path), defines);

        uint shader = _gl.CreateShader(type);
        _gl.ShaderSource(shader, source);
        _gl.CompileShader(shader);

        _gl.GetShader(shader, ShaderParameterName.CompileStatus, out int compiled);
        if (compiled == 0)
        {
            string log = _gl.GetShaderInfoLog(shader);
            _gl.DeleteShader(shader);
            throw new InvalidOperationException($"Failed to compile {type} '{path}':\n{log}");
        }

        return shader;
    }

    /// <summary>
    /// Puts the <c>#define</c> lines immediately after the <c>#version</c> directive.
    /// </summary>
    /// <remarks>
    /// Not at the top: <c>#version</c> must be the first thing in a GLSL translation unit
    /// apart from comments and whitespace, and a driver that finds anything else there
    /// rejects the whole file. A <c>#line</c> directive follows, so that the line numbers in
    /// a compile error still point at the file as it is on disk.
    /// </remarks>
    private static string Inject(string source, IReadOnlyList<string>? defines)
    {
        if (defines is null || defines.Count == 0)
        {
            return source;
        }

        int versionStart = source.IndexOf("#version", StringComparison.Ordinal);
        if (versionStart < 0)
        {
            throw new InvalidOperationException("A shader with #define symbols needs a #version directive.");
        }

        int lineEnd = source.IndexOf('\n', versionStart);
        if (lineEnd < 0)
        {
            throw new InvalidOperationException("The #version directive is the whole file.");
        }

        int versionLine = source.Take(versionStart).Count(c => c == '\n') + 1;

        var injected = new System.Text.StringBuilder(source.Length + 128);
        injected.Append(source, 0, lineEnd + 1);

        foreach (string define in defines)
        {
            injected.Append("#define ").Append(define).Append('\n');
        }

        injected.Append("#line ").Append(versionLine + 1).Append('\n');
        injected.Append(source, lineEnd + 1, source.Length - lineEnd - 1);

        return injected.ToString();
    }

    public void Dispose() => _gl.DeleteProgram(_handle);
}
