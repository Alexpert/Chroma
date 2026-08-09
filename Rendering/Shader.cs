using System.Numerics;
using Silk.NET.OpenGL;

namespace ChromaTest.Rendering;

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

    public Shader(GL gl, string vertexPath, string fragmentPath)
    {
        _gl = gl;

        uint vertex = CompileStage(ShaderType.VertexShader, vertexPath);
        uint fragment = CompileStage(ShaderType.FragmentShader, fragmentPath);

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

    private uint CompileStage(ShaderType type, string path)
    {
        string source = File.ReadAllText(path);

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

    public void Dispose() => _gl.DeleteProgram(_handle);
}
