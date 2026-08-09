using System.Numerics;
using ChromaTest.Rendering;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;

// Silk.NET.OpenGL also exposes a type named Shader, so disambiguate ours.
using Shader = ChromaTest.Rendering.Shader;

namespace ChromaTest;

internal static class Program
{
    // Resolved against the executable's folder rather than the current working
    // directory, so the app runs the same from `dotnet run`, a double-click, or a
    // debugger. The build copies Shaders/ next to the binary (see the .csproj).
    private static readonly string VertexShaderPath =
        Path.Combine(AppContext.BaseDirectory, "Shaders", "cube.vert");

    private static readonly string FragmentShaderPath =
        Path.Combine(AppContext.BaseDirectory, "Shaders", "cube.frag");

    private static IWindow _window = null!;
    private static GL _gl = null!;
    private static IInputContext _input = null!;
    private static Shader _shader = null!;
    private static Cube _cube = null!;

    // Fixed orientation: three faces stay visible so the shape reads as a cube.
    private static readonly Matrix4x4 Model =
        Matrix4x4.CreateRotationY(30f * MathF.PI / 180f) *
        Matrix4x4.CreateRotationX(20f * MathF.PI / 180f);

    private static readonly Matrix4x4 View =
        Matrix4x4.CreateLookAt(new Vector3(0f, 0f, 3f), Vector3.Zero, Vector3.UnitY);

    private static Matrix4x4 _projection;

    private static void Main()
    {
        var options = WindowOptions.Default;
        options.Size = new Vector2D<int>(1280, 720);
        options.Title = "ChromaTest";
        options.API = new GraphicsAPI(
            ContextAPI.OpenGL,
            ContextProfile.Core,
            ContextFlags.Default,
            new APIVersion(3, 3));
        options.PreferredDepthBufferBits = 24;

        _window = Window.Create(options);

        _window.Load += OnLoad;
        _window.Render += OnRender;
        _window.FramebufferResize += OnFramebufferResize;
        _window.Closing += OnClosing;

        _window.Run();
        _window.Dispose();
    }

    private static void OnLoad()
    {
        _gl = _window.CreateOpenGL();

        _input = _window.CreateInput();
        foreach (IKeyboard keyboard in _input.Keyboards)
        {
            keyboard.KeyDown += (_, key, _) =>
            {
                if (key == Key.Escape)
                {
                    _window.Close();
                }
            };
        }

        _shader = new Shader(_gl, VertexShaderPath, FragmentShaderPath);
        _cube = new Cube(_gl);

        _gl.Enable(EnableCap.DepthTest);
        _gl.ClearColor(0.08f, 0.09f, 0.11f, 1f);

        UpdateProjection(_window.FramebufferSize);
    }

    private static void OnRender(double deltaTime)
    {
        _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        _shader.Use();
        _shader.SetUniform("uModel", Model);
        _shader.SetUniform("uView", View);
        _shader.SetUniform("uProjection", _projection);

        _cube.Draw();
    }

    private static void OnFramebufferResize(Vector2D<int> size)
    {
        _gl.Viewport(size);
        UpdateProjection(size);
    }

    private static void UpdateProjection(Vector2D<int> size)
    {
        // Guard against a zero height while the window is minimised.
        float aspect = size.Y == 0 ? 1f : size.X / (float)size.Y;
        _projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 4f, aspect, 0.1f, 100f);
    }

    private static void OnClosing()
    {
        _cube.Dispose();
        _shader.Dispose();
        _input.Dispose();
        _gl.Dispose();
    }
}
