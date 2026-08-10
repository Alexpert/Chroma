using System.Diagnostics;
using System.Globalization;
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

// Imported as an alias rather than by namespace: Silk.NET.OpenGL.Extensions.ImGui ends in the
// same identifier as ImGuiNET's ImGui class, and importing both makes every use ambiguous.
using ImGuiController = Silk.NET.OpenGL.Extensions.ImGui.ImGuiController;

namespace ChromaTest;

internal static class Program
{
    private const int MaxLights = 8;   // must match MAX_LIGHTS in raytrace.frag

    /// <summary>Texture unit for the accumulation history; 0 to 3 hold the scene.</summary>
    private const int HistoryUnit = 4;

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

    private static readonly string ResolveShaderPath =
        Path.Combine(AppContext.BaseDirectory, "Shaders", "resolve.frag");

    private static readonly string ConvergenceShaderPath =
        Path.Combine(AppContext.BaseDirectory, "Shaders", "convergence.frag");

    /// <summary>Where the save button writes, relative to the working directory.</summary>
    private const string OutputDirectory = "renders";

    /// <summary>Weight of the newest frame in the displayed frame time.</summary>
    /// <remarks>
    /// A raw per-frame duration jitters far too much to read at 60 Hz. This is an exponential
    /// moving average: low enough to be steady, high enough to react when the camera or the
    /// window size changes the cost of a frame.
    /// </remarks>
    private const double FrameTimeSmoothing = 0.1;

    private static IWindow _window = null!;
    private static GL _gl = null!;
    private static IInputContext _input = null!;
    private static Shader _shader = null!;
    private static Shader _resolve = null!;
    private static Shader _convergenceShader = null!;
    private static FullscreenQuad _quad = null!;
    private static SceneBuffers _buffers = null!;
    private static AccumulationBuffer _accumulation = null!;
    private static ConvergenceMeter _convergence = null!;
    private static ImGuiController _imgui = null!;

    private static CompiledScene _scene = null!;
    private static RayBasis _rayBasis;
    private static Vector2 _invResolution = Vector2.One;

    private static string _sceneName = string.Empty;
    private static readonly Stopwatch _renderClock = new();
    private static double _frameMilliseconds;
    private static bool _saveRequested;
    private static string? _saveStatus;

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
        _sceneName = Path.GetFileNameWithoutExtension(scenePath);

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

        // The resolve and convergence stages reuse raytrace.vert: it already outputs
        // clip-space coordinates, and neither needs anything else from a vertex shader.
        _shader = new Shader(_gl, VertexShaderPath, FragmentShaderPath);
        _resolve = new Shader(_gl, VertexShaderPath, ResolveShaderPath);
        _convergenceShader = new Shader(_gl, VertexShaderPath, ConvergenceShaderPath);

        _quad = new FullscreenQuad(_gl);
        _buffers = new SceneBuffers(_gl, _scene);

        Vector2D<int> size = _window.FramebufferSize;
        _accumulation = new AccumulationBuffer(_gl, size.X, size.Y);
        _convergence = new ConvergenceMeter(_gl, size.X, size.Y);

        _imgui = new ImGuiController(_gl, _window, _input);
        Hud.Configure();

        UpdateRayBasis(size);
        _renderClock.Restart();
    }

    private static void OnRender(double deltaTime)
    {
        // Before any ImGui call: this is what starts the frame the overlay is built into.
        _imgui.Update((float)deltaTime);

        _frameMilliseconds = _frameMilliseconds == 0
            ? deltaTime * 1000.0
            : (_frameMilliseconds * (1.0 - FrameTimeSmoothing)) + (deltaTime * 1000.0 * FrameTimeSmoothing);

        TracePass();
        ResolvePass();

        _convergence.Update(
            _convergenceShader,
            _quad,
            _accumulation.ResultTexture,
            _accumulation.SampleIndex + 1);

        // The click arrives one frame late by design: it is only known once the overlay has
        // been built, and by then the overlay is what a capture would read back.
        if (_saveRequested)
        {
            _saveRequested = false;
            SaveRender();
        }

        _saveRequested = Hud.Draw(BuildStats());
        _imgui.Render();

        // Only now does this frame's result become the next frame's history.
        _accumulation.Advance();
    }

    private static HudStats BuildStats()
    {
        Vector2D<int> size = _window.FramebufferSize;

        // SampleIndex counts what the *history* holds; the frame just traced is one more.
        return new HudStats(
            size.X,
            size.Y,
            _accumulation.SampleIndex + 1,
            _scene.Scene.Render.MaxBounces,
            _renderClock.Elapsed.TotalSeconds,
            _frameMilliseconds,
            _convergence.RelativeError,
            _saveStatus);
    }

    /// <summary>Writes the current window contents to <c>renders/</c>.</summary>
    private static void SaveRender()
    {
        Vector2D<int> size = _window.FramebufferSize;

        string name = string.Create(
            CultureInfo.InvariantCulture,
            $"{_sceneName}_{_accumulation.SampleIndex + 1}spp_{DateTime.Now:yyyyMMdd-HHmmss}.png");

        string path = Path.Combine(OutputDirectory, name);

        try
        {
            ImageCapture.SaveWindow(_gl, size.X, size.Y, path);
            _saveStatus = $"saved {path}";
            Console.WriteLine($"saved {Path.GetFullPath(path)}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A full disk or a read-only directory is not a reason to lose the render in
            // progress: report it in the overlay and keep accumulating.
            _saveStatus = $"save failed: {exception.Message}";
            Console.Error.WriteLine($"error: could not save the render: {exception.Message}");
        }
    }

    /// <summary>One new sample per pixel, averaged into the accumulation buffer.</summary>
    private static void TracePass()
    {
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _accumulation.WriteFramebuffer);

        _shader.Use();

        // Uniforms are set after Use(), never before: glUniform* writes into whichever
        // program is current at that moment.
        _buffers.BindTo(_shader);
        _shader.SetUniform("uTapeLength", _scene.InstructionCount);

        _shader.SetUniform("uCameraPosition", _scene.Scene.Camera.Position);
        _shader.SetUniform("uCameraForward", _rayBasis.Forward);
        _shader.SetUniform("uCameraRight", _rayBasis.Right);
        _shader.SetUniform("uCameraUp", _rayBasis.Up);

        // Units 0 to 3 carry the scene buffers, so the history takes the next one.
        _gl.ActiveTexture(TextureUnit.Texture4);
        _gl.BindTexture(TextureTarget.Texture2D, _accumulation.HistoryTexture);
        _shader.SetUniform("uHistory", HistoryUnit);
        _shader.SetUniform("uSampleIndex", _accumulation.SampleIndex);
        _shader.SetUniform("uInvResolution", _invResolution);
        _shader.SetUniform("uMaxBounces", _scene.Scene.Render.MaxBounces);

        // Uploaded as an int rather than a bool: Shader has int, float and Vector3
        // overloads, and one more of them for a flag would not earn its place.
        _shader.SetUniform("uHasTransmission", _scene.HasTransmission ? 1 : 0);

        UploadLights();

        _quad.Draw();
    }

    /// <summary>Exposure, tone mapping and gamma, straight to the window.</summary>
    private static void ResolvePass()
    {
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);

        _resolve.Use();

        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, _accumulation.ResultTexture);
        _resolve.SetUniform("uAccumulation", 0);
        _resolve.SetUniform("uExposure", _scene.Scene.Render.Exposure);

        // No clear: the quad covers every pixel of the framebuffer.
        _quad.Draw();
    }

    private static void UploadLights()
    {
        IReadOnlyList<Light> lights = _scene.Scene.Lights;

        Span<int> kinds = stackalloc int[MaxLights];
        Span<Vector3> vectors = stackalloc Vector3[MaxLights];
        Span<Vector3> colors = stackalloc Vector3[MaxLights];
        Span<float> radii = stackalloc float[MaxLights];

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
                    radii[i] = point.Radius;
                    break;

                case DirectionalLight directional:
                    kinds[i] = 1;
                    vectors[i] = directional.Direction;
                    radii[i] = 0f;
                    break;
            }
        }

        _shader.SetUniform("uLightCount", lights.Count);
        _shader.SetUniform("uLightKind", (ReadOnlySpan<int>)kinds, lights.Count);
        _shader.SetUniform("uLightVector", (ReadOnlySpan<Vector3>)vectors, lights.Count);
        _shader.SetUniform("uLightColor", (ReadOnlySpan<Vector3>)colors, lights.Count);
        _shader.SetUniform("uLightRadius", (ReadOnlySpan<float>)radii, lights.Count);
    }

    private static void OnFramebufferResize(Vector2D<int> size)
    {
        _gl.Viewport(size);
        UpdateRayBasis(size);

        // Every sample taken so far described different pixels, so none of them survive.
        _accumulation.Resize(size.X, size.Y);
        _convergence.Resize(size.X, size.Y);

        // The stats describe the accumulation, so they restart with it.
        _renderClock.Restart();
        _frameMilliseconds = 0;
    }

    private static void UpdateRayBasis(Vector2D<int> size)
    {
        // Guard against a zero height while the window is minimised: size.X / 0f is
        // infinity, which propagates into the basis and yields NaN directions from then on
        // -- the image never comes back after the window is restored.
        float aspect = size.Y == 0 ? 1f : size.X / (float)size.Y;
        _rayBasis = _scene.Scene.Camera.CreateRayBasis(aspect);

        _invResolution = new Vector2(
            1f / Math.Max(size.X, 1),
            1f / Math.Max(size.Y, 1));
    }

    private static void OnClosing()
    {
        _imgui.Dispose();
        _convergence.Dispose();
        _accumulation.Dispose();
        _buffers.Dispose();
        _quad.Dispose();
        _convergenceShader.Dispose();
        _resolve.Dispose();
        _shader.Dispose();
        _input.Dispose();
        _gl.Dispose();
    }
}
