using System.Numerics;
using ChromaTest.Core;
using ChromaTest.Core.Compilation;
using ChromaTest.Core.Model;
using ChromaTest.Core.Model.Lighting;
using ChromaTest.Core.Sdl.Source;
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
    private const int MaxLights = 8;   // must match MAX_LIGHTS in raytrace.frag

    private const int ExitSuccess = 0;
    private const int ExitSceneHasErrors = 1;
    private const int ExitBadUsage = 2;

    // Resolved against the executable's folder rather than the current working directory,
    // so the app runs the same from `dotnet run`, a double-click, or a debugger. The build
    // copies Shaders/ next to the binary (see the .csproj). Scene files are the opposite
    // case: they are user data named on the command line and resolve as given.
    private static readonly string VertexShaderPath =
        Path.Combine(AppContext.BaseDirectory, "Shaders", "raytrace.vert");

    private static readonly string FragmentShaderPath =
        Path.Combine(AppContext.BaseDirectory, "Shaders", "raytrace.frag");

    private static IWindow _window = null!;
    private static GL _gl = null!;
    private static IInputContext _input = null!;
    private static Shader _shader = null!;
    private static FullscreenQuad _quad = null!;
    private static SceneBuffers _buffers = null!;

    private static CompiledScene _scene = null!;
    private static RayBasis _rayBasis;

    private static int Main(string[] args)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine("Usage: ChromaTest <scene-file>");
            return ExitBadUsage;
        }

        string path = args[0];

        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"error: no such file '{path}'");
            return ExitBadUsage;
        }

        CompiledScene? compiled;
        IReadOnlyList<Diagnostic> diagnostics;

        try
        {
            SceneLoader.TryLoadCompiled(path, out compiled, out diagnostics);
        }
        catch (IOException exception)
        {
            Console.Error.WriteLine($"error: could not read '{path}': {exception.Message}");
            return ExitBadUsage;
        }

        foreach (Diagnostic diagnostic in diagnostics)
        {
            Console.Error.WriteLine(diagnostic.ToString());
        }

        // Loading fails before any window exists. A scene error belongs on the console,
        // not behind a black window the user has to close to read it.
        if (compiled is null)
        {
            int errors = diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error);
            Console.Error.WriteLine($"{errors} error{(errors == 1 ? "" : "s")}; nothing to render.");
            return ExitSceneHasErrors;
        }

        if (compiled.Scene.Lights.Count > MaxLights)
        {
            Console.Error.WriteLine(
                $"error: the scene declares {compiled.Scene.Lights.Count} lights; the shader supports {MaxLights}.");
            return ExitSceneHasErrors;
        }

        _scene = compiled;
        Console.WriteLine(
            $"{Path.GetFileName(path)}: {_scene.PrimitiveCount} primitives, "
            + $"{_scene.MaterialCount} materials, {_scene.Scene.Lights.Count} lights");

        Run(path);
        return ExitSuccess;
    }

    private static void Run(string scenePath)
    {
        var options = WindowOptions.Default;
        options.Size = new Vector2D<int>(1280, 720);
        options.Title = $"ChromaTest - {Path.GetFileName(scenePath)}";
        options.API = new GraphicsAPI(
            ContextAPI.OpenGL,
            ContextProfile.Core,
            ContextFlags.Default,
            new APIVersion(3, 3));

        // No depth buffer is requested and no depth test is enabled: a fullscreen quad has
        // no depth complexity, and visibility is resolved analytically along each ray.

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
        _quad = new FullscreenQuad(_gl);
        _buffers = new SceneBuffers(_gl, _scene);

        _gl.ClearColor(0.08f, 0.09f, 0.11f, 1f);

        UpdateRayBasis(_window.FramebufferSize);
    }

    private static void OnRender(double deltaTime)
    {
        _gl.Clear(ClearBufferMask.ColorBufferBit);

        _shader.Use();

        // Uniforms are set after Use(), never before: glUniform* writes into whichever
        // program is current at that moment.
        _buffers.BindTo(_shader);
        _shader.SetUniform("uTapeLength", _scene.InstructionCount);

        _shader.SetUniform("uCameraPosition", _scene.Scene.Camera.Position);
        _shader.SetUniform("uCameraForward", _rayBasis.Forward);
        _shader.SetUniform("uCameraRight", _rayBasis.Right);
        _shader.SetUniform("uCameraUp", _rayBasis.Up);

        UploadLights();

        _quad.Draw();
    }

    private static void UploadLights()
    {
        IReadOnlyList<Light> lights = _scene.Scene.Lights;

        Span<int> kinds = stackalloc int[MaxLights];
        Span<Vector3> vectors = stackalloc Vector3[MaxLights];
        Span<Vector3> colors = stackalloc Vector3[MaxLights];

        for (int i = 0; i < lights.Count; i++)
        {
            Light light = lights[i];

            // Intensity is folded into the colour here so the shader has one fewer array
            // to fetch and one fewer multiply per light.
            colors[i] = light.Color * light.Intensity;

            switch (light)
            {
                case PointLight point:
                    kinds[i] = 0;
                    vectors[i] = point.Position;
                    break;

                case DirectionalLight directional:
                    kinds[i] = 1;
                    vectors[i] = directional.Direction;
                    break;
            }
        }

        _shader.SetUniform("uLightCount", lights.Count);
        _shader.SetUniform("uLightKind", (ReadOnlySpan<int>)kinds, lights.Count);
        _shader.SetUniform("uLightVector", (ReadOnlySpan<Vector3>)vectors, lights.Count);
        _shader.SetUniform("uLightColor", (ReadOnlySpan<Vector3>)colors, lights.Count);
    }

    private static void OnFramebufferResize(Vector2D<int> size)
    {
        _gl.Viewport(size);
        UpdateRayBasis(size);
    }

    private static void UpdateRayBasis(Vector2D<int> size)
    {
        // Guard against a zero height while the window is minimised: size.X / 0f is
        // infinity, which propagates into the basis and yields NaN directions from then on
        // -- the image never comes back after the window is restored.
        float aspect = size.Y == 0 ? 1f : size.X / (float)size.Y;
        _rayBasis = _scene.Scene.Camera.CreateRayBasis(aspect);
    }

    private static void OnClosing()
    {
        _buffers.Dispose();
        _quad.Dispose();
        _shader.Dispose();
        _input.Dispose();
        _gl.Dispose();
    }
}
