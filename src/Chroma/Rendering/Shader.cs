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
    /// <param name="geometry">
    /// The scene's generated GLSL, spliced in at the fragment stage's <c>@chroma:geometry</c>
    /// marker. Null leaves the file as it is on disk, which is what the resolve and convergence
    /// stages want.
    /// </param>
    /// <param name="version">
    /// The GLSL version to compile the fragment stage as, e.g. <c>"460 core"</c>. Null keeps
    /// whatever the file on disk declares, which is what every stage but the tracer wants: the
    /// resolve, convergence and HUD stages are 330 and have no reason not to be.
    /// </param>
    public Shader(
        GL gl,
        string vertexPath,
        string fragmentPath,
        IReadOnlyList<string>? defines = null,
        string? geometry = null,
        string? version = null)
    {
        _gl = gl;

        uint vertex = CompileStage(ShaderType.VertexShader, vertexPath);
        uint fragment = CompileStage(ShaderType.FragmentShader, fragmentPath, defines, geometry, version);

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

    private Shader(GL gl, uint handle)
    {
        _gl = gl;
        _handle = handle;
    }

    /// <summary>
    /// The same tracer, compiled as a compute shader: one stage, no vertex program, no quad.
    /// </summary>
    /// <remarks>
    /// A compute program is a program like any other once it is linked — the uniform plumbing
    /// above is unchanged — so this differs from the constructor only in having one stage to
    /// attach instead of two. It exists because rasterising a fullscreen quad to run a path
    /// tracer was always a way of getting at the hardware rather than a use of it, and because
    /// NVIDIA's fragment pipeline caps a program at about 65,000 assembly instructions, which a
    /// generated scene reaches.
    /// </remarks>
    public static Shader Compute(
        GL gl,
        string path,
        IReadOnlyList<string>? defines,
        string? geometry,
        string version)
    {
        uint stage = Compile(gl, ShaderType.ComputeShader, path, defines, geometry, version);

        uint handle = gl.CreateProgram();
        gl.AttachShader(handle, stage);
        gl.LinkProgram(handle);

        gl.GetProgram(handle, ProgramPropertyARB.LinkStatus, out int linked);
        if (linked == 0)
        {
            string log = gl.GetProgramInfoLog(handle);
            gl.DeleteProgram(handle);
            gl.DeleteShader(stage);
            throw new InvalidOperationException($"Failed to link compute program:\n{log}");
        }

        gl.DetachShader(handle, stage);
        gl.DeleteShader(stage);

        return new Shader(gl, handle);
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

    /// <summary>An <c>ivec2</c>. A resolution, in practice, which is not a pair of floats.</summary>
    public void SetUniform(string name, int x, int y) =>
        _gl.Uniform2(GetUniformLocation(name), x, y);

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

    /// <summary>
    /// The source a stage is actually compiled from: the file on disk, with the scene's
    /// <c>#define</c> symbols and its generated geometry spliced in.
    /// </summary>
    /// <remarks>
    /// Public because <c>--emit-shader</c> writes exactly this to disk. A generated shader that
    /// cannot be read is the one real cost of generating it, and one file on request is what
    /// pays that back: a driver error names a line, and the line is in a file that exists.
    /// </remarks>
    public static string Assemble(
        string path,
        IReadOnlyList<string>? defines,
        string? geometry,
        string? version = null) =>
        Splice(Inject(File.ReadAllText(path), defines, version), geometry);

    private uint CompileStage(
        ShaderType type,
        string path,
        IReadOnlyList<string>? defines = null,
        string? geometry = null,
        string? version = null) =>
        Compile(_gl, type, path, defines, geometry, version);

    private static uint Compile(
        GL gl,
        ShaderType type,
        string path,
        IReadOnlyList<string>? defines,
        string? geometry,
        string? version)
    {
        string source = Assemble(path, defines, geometry, version);

        uint shader = gl.CreateShader(type);
        gl.ShaderSource(shader, source);
        gl.CompileShader(shader);

        gl.GetShader(shader, ShaderParameterName.CompileStatus, out int compiled);
        if (compiled == 0)
        {
            string log = gl.GetShaderInfoLog(shader);
            gl.DeleteShader(shader);
            throw new InvalidOperationException($"Failed to compile {type} '{path}':\n{log}");
        }

        return shader;
    }

    /// <summary>
    /// Rewrites the <c>#version</c> directive if asked to, and puts the <c>#define</c> lines
    /// immediately after it.
    /// </summary>
    /// <remarks>
    /// Not at the top: <c>#version</c> must be the first thing in a GLSL translation unit
    /// apart from comments and whitespace, and a driver that finds anything else there
    /// rejects the whole file. A <c>#line</c> directive follows, so that the line numbers in
    /// a compile error still point at the file as it is on disk.
    /// <para>
    /// Rewriting rather than stripping keeps every shader file valid on its own: opened in an
    /// editor or fed to a validator, raytrace.glsl is a GLSL 330 fragment shader and compiles as
    /// one. Which version it is actually built at is a property of the machine it runs on, and
    /// that belongs to the host.
    /// </para>
    /// </remarks>
    private static string Inject(string source, IReadOnlyList<string>? defines, string? version)
    {
        bool hasDefines = defines is { Count: > 0 };

        if (!hasDefines && version is null)
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
        injected.Append(source, 0, versionStart);
        injected.Append("#version ").Append(version ?? source[(versionStart + "#version ".Length)..lineEnd].Trim());
        injected.Append('\n');

        if (hasDefines)
        {
            foreach (string define in defines!)
            {
                injected.Append("#define ").Append(define).Append('\n');
            }
        }

        injected.Append("#line ").Append(versionLine + 1).Append('\n');
        injected.Append(source, lineEnd + 1, source.Length - lineEnd - 1);

        return injected.ToString();
    }

    /// <summary>The marker in raytrace.glsl that the generated geometry replaces.</summary>
    private const string GeometryMarker = "// @chroma:geometry";

    /// <summary>
    /// Puts the generated geometry where the marker is.
    /// </summary>
    /// <remarks>
    /// A marker rather than an append, because order is load-bearing in GLSL: the generated
    /// code calls the primitive maths above it and is called by the path tracer below it, and
    /// there is no forward declaration to lean on. The marker line is kept as a comment so the
    /// seam is visible in an emitted file, and no <c>#line</c> is issued after it — the
    /// generated block is the part whose line numbers should point at the assembled file
    /// rather than at raytrace.glsl, which is exactly what happens if nothing intervenes.
    /// </remarks>
    private static string Splice(string source, string? geometry)
    {
        if (geometry is null)
        {
            return source;
        }

        int at = source.IndexOf(GeometryMarker, StringComparison.Ordinal);
        if (at < 0)
        {
            throw new InvalidOperationException(
                $"The fragment shader has no '{GeometryMarker}' marker to splice the scene into.");
        }

        int lineEnd = source.IndexOf('\n', at);
        return source[..(lineEnd + 1)] + "\n" + geometry + "\n" + source[(lineEnd + 1)..];
    }

    public void Dispose() => _gl.DeleteProgram(_handle);
}
