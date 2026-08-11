using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Chroma.Core;
using Chroma.Core.Compilation;
using Chroma.Core.Model;
using Chroma.Core.Model.Lighting;
using Chroma.Core.Sdl.Source;
using Chroma.Rendering;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;

// Silk.NET.OpenGL also exposes a type named Shader, so disambiguate ours.
using Shader = Chroma.Rendering.Shader;

// Imported as an alias rather than by namespace: Silk.NET.OpenGL.Extensions.ImGui ends in the
// same identifier as ImGuiNET's ImGui class, and importing both makes every use ambiguous.
using ImGuiController = Silk.NET.OpenGL.Extensions.ImGui.ImGuiController;

namespace Chroma;

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

    /// <summary>Samples to accumulate before saving and closing; 0 means run interactively.</summary>
    private static int _sampleLimit;

    /// <summary>
    /// Relative error to stop at, as a fraction; 0 means no target.
    /// </summary>
    /// <remarks>
    /// This is the metric iteration 11 is measured against, and it exists because samples per
    /// second is the wrong one: a sampler that halves the variance per sample is worth far more
    /// than a 10% sample-rate gain, and samples/s scores it as a loss if it costs anything at
    /// all. What matters is the time to reach a stated error, which is what this measures.
    /// </remarks>
    private static float _errorTarget;

    private static bool _batchSaved;

    /// <summary>Where to write the assembled fragment shader, or null not to.</summary>
    /// <remarks>
    /// The answer to "a generated shader is a shader you cannot read". It writes exactly what
    /// the driver is handed — raytrace.frag with this scene's <c>#define</c> symbols and its
    /// generated geometry spliced in — so a compile error's line number points at a file that
    /// exists, and two scenes' geometry can be diffed.
    /// </remarks>
    private static string? _emitShaderPath;

    /// <summary>Samples the timing below is measured over, and the clock it is measured on.</summary>
    /// <remarks>
    /// Separate from <see cref="_renderClock"/>, which the overlay shows and which starts when
    /// the window does. The first frame carries the driver's shader compilation — a second or
    /// so, which is a tenth of a short benchmark — so the timed run starts after it.
    /// </remarks>
    private static readonly Stopwatch _benchmarkClock = new();

    private static int _timedFrom = -1;

    private static bool Batch => _sampleLimit > 0 || _errorTarget > 0f;

    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine(
                "Usage: Chroma <scene-file> [--samples <n>] [--error <percent>] [--emit-shader <path>]");
            return ExitBadUsage;
        }

        string path = args[0];

        // A render that stops at a stated sample count or a stated noise level, saves, and
        // closes. `--samples` was pulled forward from iteration 12 because iteration 10 could
        // not be *checked* without it: every claim about a medium is a measurement on a
        // converged image, and clicking a button in an overlay does not produce one
        // reproducibly. `--error` is iteration 11's own, and it is the one that makes a
        // sampler comparable with a tracing speed-up. The window still opens -- headless
        // rendering is a different piece of work and is not this.
        for (int i = 1; i < args.Length; i++)
        {
            bool hasValue = i + 1 < args.Length;

            if (args[i] == "--samples" && hasValue)
            {
                if (!int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out _sampleLimit)
                    || _sampleLimit < 1)
                {
                    Console.Error.WriteLine("error: --samples needs a positive whole number");
                    return ExitBadUsage;
                }
            }
            else if (args[i] == "--error" && hasValue)
            {
                // Given as a percentage, because that is how the overlay reports it and how
                // every measurement in documents/performance.md is quoted.
                if (!float.TryParse(args[++i], NumberStyles.Float, CultureInfo.InvariantCulture, out float percent)
                    || percent <= 0f)
                {
                    Console.Error.WriteLine("error: --error needs a positive percentage");
                    return ExitBadUsage;
                }

                _errorTarget = percent * 0.01f;
            }
            else if (args[i] == "--emit-shader" && hasValue)
            {
                _emitShaderPath = args[++i];
            }
            else
            {
                Console.Error.WriteLine($"error: unrecognised argument '{args[i]}'");
                return ExitBadUsage;
            }
        }

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

        // The trailing list is what the trace shader is compiled with, and it is worth printing
        // because it is the single thing that most decides how fast the render will be — see
        // documents/performance.md. The widest root is the new number to watch: it is how many
        // spans a thread carries at the worst moment, and it is now a property of the scene
        // rather than a constant every scene paid for.
        string specialisation = string.Join(
            ", ",
            new[]
            {
                _scene.HasTransmission ? "transmission" : null,
                _scene.HasMedia ? "media" : null,
            }.Where(feature => feature is not null));

        Console.WriteLine(
            $"{Path.GetFileName(path)}: {_scene.PrimitiveCount} primitives, "
            + $"{_scene.MaterialCount} materials, {_scene.Scene.Lights.Count} lights, "
            + $"{_scene.GeneratedLines} generated lines, widest root {_scene.WidestRoot} spans"
            + (specialisation.Length > 0 ? $"; shader carries {specialisation}" : "; lean shader"));

        Run(path);
        return ExitSuccess;
    }

    private static void Run(string scenePath)
    {
        _sceneName = Path.GetFileNameWithoutExtension(scenePath);

        var options = WindowOptions.Default;
        options.Size = new Vector2D<int>(1280, 720);
        options.Title = $"Chroma - {Path.GetFileName(scenePath)}";

        // WindowOptions.Default asks for vertical sync, which is right for an application that
        // redraws the same picture and wrong for one where a frame IS a sample: it makes the
        // monitor's refresh rate the sample rate, and a scene cheaper than one refresh converges
        // no faster than one that is exactly that expensive.
        //
        // It was tested at the start of iteration 11 and made no difference to any scene, and
        // that finding expired during the iteration: chamber.chroma and primitives.chroma now
        // both land within a percent of 60 samples per second, which is not a coincidence. The
        // image is unaffected -- this changes how often samples are taken, never what they are.
        options.VSync = false;

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

        // The trace shader is compiled for this scene and no other. What each symbol buys is
        // in raytrace.frag; what they have in common is that they are all questions the scene
        // answers once, and a shader this close to the occupancy cliff would rather not be
        // compiled with the answer it does not need.
        string[] defines =
        [
            $"CHROMA_TRANSMISSION {(_scene.HasTransmission ? 1 : 0)}",
            $"CHROMA_MEDIA {(_scene.HasMedia ? 1 : 0)}",
        ];

        if (_emitShaderPath is not null)
        {
            File.WriteAllText(
                _emitShaderPath,
                Shader.Assemble(FragmentShaderPath, defines, _scene.Geometry));
            Console.WriteLine($"wrote {_emitShaderPath}");
        }

        // The resolve and convergence stages reuse raytrace.vert: it already outputs
        // clip-space coordinates, and neither needs anything else from a vertex shader.
        _shader = new Shader(_gl, VertexShaderPath, FragmentShaderPath, defines, _scene.Geometry);
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

            if (_batchSaved)
            {
                _window.Close();
                return;
            }
        }

        _saveRequested = Hud.Draw(BuildStats());
        _imgui.Render();

        // Only now does this frame's result become the next frame's history.
        _accumulation.Advance();

        if (!Batch)
        {
            return;
        }

        // Without this the wall clock measures how fast commands are *queued*, not how fast
        // they run: the driver buffers several frames ahead, so a benchmark ends with work
        // still outstanding and reports a rate the hardware never achieved.
        _gl.Finish();

        if (_timedFrom < 0)
        {
            // The first frame carries the driver's compilation of the fragment shader, which
            // is seconds on a program this size and belongs to no sample.
            _timedFrom = _accumulation.SampleIndex;
            _benchmarkClock.Restart();
        }

        // Batch mode asks for the save the same way a click does, and for the same reason:
        // the capture reads the back buffer, so it has to happen at the top of the next
        // frame, after the resolve pass and before the overlay is drawn over it.
        if (!_batchSaved && BatchTargetReached())
        {
            _batchSaved = true;
            _saveRequested = true;
            ReportBenchmark();
        }
    }

    /// <summary>Whether the batch run has done what it was asked for.</summary>
    private static bool BatchTargetReached()
    {
        if (_sampleLimit > 0 && _accumulation.SampleIndex >= _sampleLimit)
        {
            return true;
        }

        // NaN until the first measurement, and NaN compares false, so an unlit frame never
        // ends the run by accident.
        return _errorTarget > 0f && _convergence.RelativeError <= _errorTarget;
    }

    /// <summary>
    /// One line per run, on stdout: what iteration 11 is judged by.
    /// </summary>
    private static void ReportBenchmark()
    {
        double seconds = _benchmarkClock.Elapsed.TotalSeconds;
        int timed = _accumulation.SampleIndex - Math.Max(_timedFrom, 0);
        double rate = seconds > 0 ? timed / seconds : 0;

        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"benchmark {_sceneName}: {_accumulation.SampleIndex} samples, "
            + $"{seconds:F2} s over the last {timed}, {rate:F1} samples/s, "
            + $"error {_convergence.RelativeError * 100f:F3}%"));
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

        // Transmission and media used to be uniforms set here. They are #define symbols now,
        // fixed when the program was compiled in OnLoad -- see the define list there.
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
