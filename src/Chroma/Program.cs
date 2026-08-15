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
    private const int MaxLights = 8;   // must match MAX_LIGHTS in raytrace.glsl

    /// <summary>Texture unit for the accumulation history on the fallback path; 0 to 3 hold the scene.</summary>
    private const int HistoryUnit = 5;

    /// <summary>Image binding for the accumulation target on the compute path.</summary>
    private const uint AccumulationBinding = 0;

    /// <summary>Must match local_size_x and local_size_y in raytrace.glsl.</summary>
    private const int WorkgroupSize = 8;

    private const int ExitSuccess = 0;
    private const int ExitSceneHasErrors = 1;
    private const int ExitBadUsage = 2;

    /// <summary>The driver reset under us, which is a machine limit rather than a scene error.</summary>
    private const int ExitDriverReset = 3;

    // Resolved against the executable's folder rather than the current working directory,
    // so the app runs the same from `dotnet run`, a double-click, or a debugger. The build
    // copies Shaders/ next to the binary (see the .csproj). Scene files are the opposite
    // case: they are user data named on the command line and resolve as given.
    private static readonly string VertexShaderPath =
        Path.Combine(AppContext.BaseDirectory, "Shaders", "raytrace.vert");

    /// <summary>The tracer, compiled as a fragment or a compute shader depending on the tier.</summary>
    private static readonly string TraceShaderPath =
        Path.Combine(AppContext.BaseDirectory, "Shaders", "raytrace.glsl");

    private static readonly string ResolveShaderPath =
        Path.Combine(AppContext.BaseDirectory, "Shaders", "resolve.frag");

    private static readonly string ConvergenceShaderPath =
        Path.Combine(AppContext.BaseDirectory, "Shaders", "convergence.frag");

    /// <summary>Where the save button writes, relative to the working directory.</summary>
    private const string OutputDirectory = "renders";

    private const int DefaultWidth = 1280;
    private const int DefaultHeight = 720;

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

    /// <summary>Exact file the batch render writes, or null for the dated name in <c>renders/</c>.</summary>
    /// <remarks>
    /// A timestamp in the name is right for a render someone made while using the tool and wrong
    /// for one a script produced: the manual's illustrations have to land on the same paths
    /// every time, or regenerating them is a commit rather than a check.
    /// </remarks>
    private static string? _outputPath;

    /// <summary>Framebuffer the window asks for.</summary>
    private static int _width = DefaultWidth;
    private static int _height = DefaultHeight;

    /// <summary><c>--headless</c>: create the window without showing it.</summary>
    /// <remarks>
    /// The context, the framebuffer and the readback are the ones the visible path uses -- only
    /// the mapping to the screen is skipped, and with it the overlay, which has nothing to be
    /// drawn over. Sixty illustrations that each steal focus for a second is the difference
    /// between a script someone runs and one they avoid running.
    /// </remarks>
    private static bool _headless;

    /// <summary>Where to write the assembled trace shader, or null not to.</summary>
    /// <remarks>
    /// The answer to "a generated shader is a shader you cannot read". It writes exactly what
    /// the driver is handed — raytrace.glsl with this scene's <c>#define</c> symbols and its
    /// generated geometry spliced in — so a compile error's line number points at a file that
    /// exists, and two scenes' geometry can be diffed.
    /// </remarks>
    private static string? _emitShaderPath;

    /// <summary><c>--compute</c>: run the tracer as a compute shader where the machine allows it.</summary>
    /// <remarks>
    /// Opt-in rather than automatic, and that is a measurement rather than a preference. The
    /// compute path is the newer and tidier one — storage buffers, an accumulation image written
    /// in place, no quad to rasterise — and on this hardware it is a wash: within a few percent
    /// on eleven of thirteen scenes, and 3.5x SLOWER on sweeps.chroma, whose 24-span root is the
    /// heaviest register load in the set. Until that is understood, the path that has been
    /// measured for twelve iterations stays the default. See documents/gpu-backends.md.
    /// </remarks>
    private static bool _useCompute;

    /// <summary><c>--tbo</c>: read the scene tables through a sampler on the compute tier too.</summary>
    private static bool _forceTextureBuffers;

    /// <summary><c>--sdf</c>: find geometry by sphere tracing a distance field instead of by
    /// exact intervals.</summary>
    /// <remarks>
    /// A demonstrator, not an alternative anyone should render with. It exists because iteration
    /// 0 chose exact intervals over distance fields on reasoning alone and nothing ever tested
    /// the choice; both backends fill the same generated slot and answer the same
    /// <c>traceScene</c>, so the path tracer above them is shared and the two differ in exactly
    /// one thing. See documents/raymarching.md.
    /// </remarks>
    private static bool _useDistanceField;

    /// <summary><c>--march</c>: how many sphere-tracing steps a ray may take.</summary>
    /// <remarks>
    /// A uniform rather than a constant in the shader, which is not tuning but survival: the
    /// driver unrolls a loop with a compile-time bound, and each copy would carry the whole
    /// scene's field function. See documents/gpu-backends.md.
    /// </remarks>
    private static int _marchSteps = 128;

    /// <summary><c>--enhanced</c>: over-relaxed marching rather than the plain sphere trace.</summary>
    private static bool _enhancedMarch;

    /// <summary>What the context that arrived can do, and which path was chosen from it.</summary>
    private static GlCapabilities _capabilities = null!;

    /// <summary>Whether <c>glGetGraphicsResetStatus</c> exists on the context that arrived.</summary>
    private static bool _resetQueryable;

    /// <summary>Set once a reset is observed, so nothing tries to tidy up a dead context.</summary>
    private static bool _contextLost;

    /// <summary>Whether the first frame has been timed, so its warning is said once.</summary>
    private static bool _firstFrameTimed;

    /// <summary>Render callbacks so far, only to find the one that carries the first frame's cost.</summary>
    private static int _framesRendered;

    /// <summary>What <see cref="Run"/> returns, so a failure inside the render loop can set it.</summary>
    private static int _exitCode = ExitSuccess;

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
                "Usage: Chroma <scene-file> [--samples <n>] [--error <percent>]\n"
                + "               [--output <path>] [--size <w>x<h>] [--headless]\n"
                + "               [--emit-shader <path>] [--compute] [--tbo]\n"
                + "               [--sdf] [--enhanced] [--march <n>]");
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
            else if (args[i] == "--output" && hasValue)
            {
                _outputPath = args[++i];
            }
            else if (args[i] == "--size" && hasValue)
            {
                if (!TryParseSize(args[++i], out _width, out _height))
                {
                    Console.Error.WriteLine("error: --size needs two positive whole numbers, as <w>x<h>");
                    return ExitBadUsage;
                }
            }
            else if (args[i] == "--headless")
            {
                _headless = true;
            }
            else if (args[i] == "--emit-shader" && hasValue)
            {
                _emitShaderPath = args[++i];
            }
            else if (args[i] == "--compute")
            {
                _useCompute = true;
            }
            else if (args[i] == "--tbo")
            {
                // Reads the scene tables through a sampler on the compute path too. Only a
                // measurement lever: which of the two is faster is not deducible.
                _forceTextureBuffers = true;
            }
            else if (args[i] == "--sdf")
            {
                _useDistanceField = true;
            }
            else if (args[i] == "--enhanced")
            {
                _enhancedMarch = true;
            }
            else if (args[i] == "--march" && hasValue)
            {
                if (!int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out _marchSteps)
                    || _marchSteps < 1)
                {
                    Console.Error.WriteLine("error: --march needs a positive whole number");
                    return ExitBadUsage;
                }
            }
            else
            {
                Console.Error.WriteLine($"error: unrecognised argument '{args[i]}'");
                return ExitBadUsage;
            }
        }

        // Both of these describe a run that ends on its own. Without a target the window is the
        // only thing that ends it, and a window nobody can see is the failure this project has
        // refused since iteration 1: no picture, no message, and nothing to close.
        if (!Batch && (_headless || _outputPath is not null))
        {
            Console.Error.WriteLine(
                "error: --headless and --output need a run that ends by itself; add --samples or --error");
            return ExitBadUsage;
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
            SceneLoader.TryLoadCompiled(
                path,
                out compiled,
                out diagnostics,
                _useDistanceField ? GeometryBackend.DistanceField : GeometryBackend.Spans);
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

        // The shape count is printed next to the primitive count because it is now the number
        // that decides whether a scene compiles at all: the program holds one body per distinct
        // shape, and a placement of a shape already emitted costs a record in a buffer instead.
        // A board of thirty-two pieces and a board of ten thousand are the same size of shader.
        string placement = _scene.InstanceCount > 0
            ? $"{_scene.ShapeCount} shapes, {_scene.InstanceCount} instances"
            : $"{_scene.ShapeCount} shapes";

        // The estimate, so that how close a scene is to the wall is visible before it hits it
        // rather than only in the message that says it did. It is an estimate and the driver is
        // the authority, which is why this is a percentage of a budget rather than a count of
        // instructions pretending to be exact. See Compilation/ShapeCost.
        string budget = _scene.ShapeReports.Count == 0
            ? string.Empty
            : $", estimated {_scene.EstimatedCost} statements "
                + $"({ShapeCost.Share(_scene.EstimatedCost)} of the instruction budget)";

        Console.WriteLine(
            $"{Path.GetFileName(path)}: {_scene.PrimitiveCount} primitives, {placement}, "
            + $"{_scene.MaterialCount} materials, {_scene.Scene.Lights.Count} lights, "
            + $"{_scene.GeneratedLines} generated lines, widest root {_scene.WidestRoot} spans"
            + budget
            + (specialisation.Length > 0 ? $"; shader carries {specialisation}" : "; lean shader"));

        return Run(path);
    }

    /// <summary>Reads <c>--size</c>'s <c>&lt;w&gt;x&lt;h&gt;</c>.</summary>
    private static bool TryParseSize(string text, out int width, out int height)
    {
        width = 0;
        height = 0;

        int separator = text.IndexOf('x', StringComparison.OrdinalIgnoreCase);
        if (separator < 0)
        {
            return false;
        }

        return int.TryParse(
                   text[..separator], NumberStyles.Integer, CultureInfo.InvariantCulture, out width)
               && int.TryParse(
                   text[(separator + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out height)
               && width > 0
               && height > 0;
    }

    private static int Run(string scenePath)
    {
        _sceneName = Path.GetFileNameWithoutExtension(scenePath);

        var options = WindowOptions.Default;
        options.Size = new Vector2D<int>(_width, _height);
        options.Title = $"Chroma - {Path.GetFileName(scenePath)}";

        // A window that is never mapped to the screen. The context, the framebuffer and the
        // readback are the ones the visible path uses -- only the presentation is skipped, and
        // with it the overlay, which would have nothing to be drawn over. This is what lets a
        // script produce the manual's illustrations without taking the screen for a minute.
        options.IsVisible = !_headless;

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

        // Ask for the newest context the machine will give. A driver is free to hand back
        // something newer than requested but never something older, so this is a floor, not a
        // choice: GlCapabilities reads back what actually arrived and decides from that. The
        // fallback is genuine -- OpenGL 3.3 is what this renderer targeted for twelve iterations
        // and still renders every scene that fits its instruction budget.
        //
        // macOS is the exception, and it is not a preference: there the request is not a floor.
        // Apple stops at 4.1 and only gives 3.2 and above through a forward-compatible core
        // profile, so asking for 4.6 fails to create the context rather than returning less.
        // The reasoning is with the constants, in GlCapabilities.
        options.API = OperatingSystem.IsMacOS()
            ? new GraphicsAPI(
                ContextAPI.OpenGL,
                ContextProfile.Core,
                ContextFlags.ForwardCompatible,
                new APIVersion(GlCapabilities.MacMajor, GlCapabilities.MacMinor))
            : new GraphicsAPI(
                ContextAPI.OpenGL,
                ContextProfile.Core,
                ContextFlags.Default,
                new APIVersion(GlCapabilities.PreferredMajor, GlCapabilities.PreferredMinor));

        // No depth buffer is requested and no depth test is enabled: a fullscreen quad has
        // no depth complexity, and visibility is resolved analytically along each ray.

        _window = Window.Create(options);

        _window.Load += OnLoad;
        _window.Render += OnRender;
        _window.FramebufferResize += OnFramebufferResize;
        _window.Closing += OnClosing;

        // What reaches here is whatever the GL layer manages to throw. It is NOT assumed to be a
        // driver reset: the query below is asked first and only its answer is reported as one,
        // because blaming the driver for an ordinary bug would send the reader looking in the
        // wrong place. The reset that kills the process outright reaches neither this nor the
        // per-frame poll, which is why the warning before the first frame exists.
        try
        {
            _window.Run();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            string? cause = PollAfterFailure();

            if (cause is not null)
            {
                ReportGraphicsReset(cause);
                _contextLost = true;
            }
            else
            {
                Console.Error.WriteLine($"error: the render loop failed: {exception.Message}");
            }

            _exitCode = ExitDriverReset;
        }

        _window.Dispose();
        return _exitCode;
    }

    /// <summary>
    /// Asks the driver whether it reset, from inside a failure where the context may be unusable.
    /// </summary>
    /// <remarks>
    /// Null covers both "healthy" and "could not be asked", and the caller treats them the same:
    /// neither is evidence of a reset, so neither may be reported as one.
    /// </remarks>
    private static string? PollAfterFailure()
    {
        if (!_resetQueryable)
        {
            return null;
        }

        try
        {
            return GraphicsReset.Poll(_gl);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Prints what a driver reset is, why this scene provoked one, and what to change.
    /// </summary>
    /// <param name="cause">
    /// The driver's own attribution, or null when the reset was inferred from the process failing
    /// rather than observed through the query.
    /// </param>
    private static void ReportGraphicsReset(string? cause)
    {
        Vector2D<int> size = _window.FramebufferSize;

        Console.Error.WriteLine();
        Console.Error.WriteLine(GraphicsReset.Explain(
            cause,
            size.X > 0 ? size.X : _width,
            size.Y > 0 ? size.Y : _height,
            _useDistanceField,
            _marchSteps,
            Batch));
    }

    private static void OnLoad()
    {
        _gl = _window.CreateOpenGL();
        _capabilities = GlCapabilities.Detect(_gl, _useCompute, _forceTextureBuffers);
        Console.WriteLine(_capabilities.Describe());

        // Core in 4.5, absent below it, and calling a function the context does not have is not
        // an error the driver reports politely. Whether it ever answers anything but "healthy" is
        // a second question the remarks on GraphicsReset take up: a driver need only report
        // through it when the context asked to be told, and Silk.NET offers no way to ask. It is
        // kept because it costs one integer query a frame and because when it does fire it is the
        // only way to say what happened before the objects are gone.
        _resetQueryable = GraphicsReset.IsQueryable(_capabilities);

        // Said before the first frame because the first frame is what may kill the process, and a
        // fatal native abort leaves nothing able to print afterwards.
        if (GraphicsReset.Caution(_useDistanceField) is string caution)
        {
            Console.Error.WriteLine(caution);
        }

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

        try
        {
            _shader = CompileTracer();
        }
        catch (InvalidOperationException exception) when (GlCapabilities.IsOverflow(exception.Message))
        {
            // The one driver failure a scene author can act on, so it is said in those terms
            // rather than as an assembly line number in a program nobody has.
            Console.Error.WriteLine(
                $"error: {_capabilities.ExplainOverflow(_scene, exception.Message)}");
            Environment.Exit(ExitSceneHasErrors);
        }

        // The resolve and convergence stages reuse raytrace.vert: it already outputs clip-space
        // coordinates, and neither needs anything else from a vertex shader. They stay at the
        // version they declare -- only the tracer is compiled at the tier's.
        _resolve = new Shader(_gl, VertexShaderPath, ResolveShaderPath);
        _convergenceShader = new Shader(_gl, VertexShaderPath, ConvergenceShaderPath);

        _quad = new FullscreenQuad(_gl);
        _buffers = new SceneBuffers(_gl, _scene, _capabilities.UseStorageBuffers);

        Vector2D<int> size = _window.FramebufferSize;
        _accumulation = new AccumulationBuffer(_gl, size.X, size.Y, _capabilities.IsCompute);
        _convergence = new ConvergenceMeter(_gl, size.X, size.Y);

        if (!_headless)
        {
            _imgui = new ImGuiController(_gl, _window, _input);
            Hud.Configure();
        }

        UpdateRayBasis(size);
        _renderClock.Restart();
    }

    /// <summary>
    /// Compiles the tracer, and if the driver says it is too large, shares more of what repeats
    /// and asks again.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A shape is reached through the instance buffer once it appears often enough to be worth the
    /// indirection — see <see cref="ShapePartition.DefaultShareFrom"/> — or once the budget says
    /// the program will not fit otherwise. Both are guesses made before the driver has seen
    /// anything, and this is what happens when a guess costs a scene the program.
    /// </para>
    /// <para>
    /// It has to be answered here because the driver is the only authority on how large a program
    /// may be, and it does not speak until the scene has been compiled and handed over. The
    /// estimate exists to make a good first guess and to be able to say <i>which</i> shapes are
    /// expensive; it does not replace asking. What the estimate is not is a line count, which was
    /// measured not to predict this at all: documents/gpu-backends.md records a 5,002-line program
    /// refused and a 6,436-line one accepted.
    /// </para>
    /// <para>
    /// Each attempt cuts the budget to four fifths of what was just refused, which forces the
    /// greedy in <see cref="ShapePartition.Choose"/> to shed its most expensive repeated shape or
    /// two, rather than throwing away the folded form on every shape in the scene at the first
    /// sign of trouble. If that is still not enough, the last attempt shares everything shareable,
    /// which is the smallest program this can produce.
    /// </para>
    /// </remarks>
    private static Shader CompileTracer()
    {
        // Three compiles at the very most. A near-ceiling program takes seconds to be refused, and
        // a startup that spends a minute converging on a threshold is its own kind of failure.
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return BuildTracer();
            }
            catch (InvalidOperationException exception)
                when (GlCapabilities.IsOverflow(exception.Message) && attempt < 2)
            {
                int budget = attempt == 0 ? _scene.EstimatedCost * 4 / 5 : 0;
                int shareFrom = attempt == 0 ? _scene.ShareFrom : ShapePartition.ShareEverything;

                CompiledScene smaller = SceneLoader.Recompile(
                    _scene,
                    shareFrom,
                    budget,
                    _useDistanceField ? GeometryBackend.DistanceField : GeometryBackend.Spans);

                // A scene whose geometry is all different has nothing to share, and asking the
                // driver the same question again would cost another near-ceiling compile to be
                // told the same thing. scenes/cube.chroma is that scene: eight thousand boxes in
                // one union is one shape with eight thousand leaves, not eight thousand
                // placements of one shape, and instancing has nothing to offer it.
                if (smaller.EstimatedCost >= _scene.EstimatedCost)
                {
                    throw;
                }

                Console.Error.WriteLine(
                    $"note: the driver refused this scene at an estimated {_scene.EstimatedCost} "
                    + $"statements. Sharing more of what repeats brings it to "
                    + $"{smaller.EstimatedCost}, over {smaller.InstanceCount} placements of "
                    + $"{smaller.ShapeCount} shapes.");

                _scene = smaller;
            }
        }
    }

    /// <summary>Assembles and compiles the tracer for the scene as it currently stands.</summary>
    private static Shader BuildTracer()
    {
        // The trace shader is compiled for this scene and no other. What each symbol buys is
        // in raytrace.glsl; what they have in common is that they are all questions the scene
        // answers once, and a shader this close to the occupancy cliff would rather not be
        // compiled with the answer it does not need.
        string[] defines =
        [
            $"CHROMA_TRANSMISSION {(_scene.HasTransmission ? 1 : 0)}",
            $"CHROMA_MEDIA {(_scene.HasMedia ? 1 : 0)}",
            $"CHROMA_COMPUTE {(_capabilities.IsCompute ? 1 : 0)}",
            $"CHROMA_STORAGE_BUFFERS {(_capabilities.UseStorageBuffers ? 1 : 0)}",
            $"CHROMA_SDF {(_useDistanceField ? 1 : 0)}",
            $"CHROMA_SDF_ENHANCED {(_useDistanceField && _enhancedMarch ? 1 : 0)}",

            // Read off the table that was packed rather than off what the scene looked like, the
            // same discipline as HasTransmission: the shader declares the instance buffers when
            // and only when SceneBuffers creates them, because both ask this one question.
            $"CHROMA_INSTANCES {(_scene.InstanceCount > 0 ? 1 : 0)}",
        ];

        if (_emitShaderPath is not null)
        {
            File.WriteAllText(
                _emitShaderPath,
                Shader.Assemble(TraceShaderPath, defines, _scene.Geometry, _capabilities.GlslVersion));
            Console.WriteLine($"wrote {_emitShaderPath}");
        }

        return _capabilities.IsCompute
            ? Shader.Compute(_gl, TraceShaderPath, defines, _scene.Geometry, _capabilities.GlslVersion)
            : new Shader(
                _gl,
                VertexShaderPath,
                TraceShaderPath,
                defines,
                _scene.Geometry,
                _capabilities.GlslVersion);
    }

    private static void OnRender(double deltaTime)
    {
        // Before any ImGui call: this is what starts the frame the overlay is built into.
        if (!_headless)
        {
            _imgui.Update((float)deltaTime);
        }

        _frameMilliseconds = _frameMilliseconds == 0
            ? deltaTime * 1000.0
            : (_frameMilliseconds * (1.0 - FrameTimeSmoothing)) + (deltaTime * 1000.0 * FrameTimeSmoothing);

        TracePass();
        ResolvePass();

        // Asked once per frame, right after the work that could have caused it. A reset leaves
        // every GL object gone, so there is nothing to salvage and nothing to retry: say what
        // happened, say what to change, and stop. Continuing would spend the rest of the run
        // drawing into buffers that no longer exist.
        if (_resetQueryable && GraphicsReset.Poll(_gl) is string cause)
        {
            ReportGraphicsReset(cause);
            _contextLost = true;
            _exitCode = ExitDriverReset;
            _window.Close();
            return;
        }

        // Measured rather than guessed, and said once. A frame this long survived, so the reader
        // is still here to read it, and is one heavier scene from not being.
        //
        // On the SECOND callback, not the first: deltaTime is the gap since the previous render,
        // so the first one reports the time before any tracing happened -- near zero, whatever the
        // scene costs. The first frame's real duration is what arrives here next time round.
        if (!_firstFrameTimed && ++_framesRendered == 2)
        {
            _firstFrameTimed = true;
            Vector2D<int> frame = _window.FramebufferSize;

            if (GraphicsReset.AfterFirstFrame(deltaTime, frame.X, frame.Y) is string warning)
            {
                Console.Error.WriteLine(warning);
            }
        }

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

        if (!_headless)
        {
            _saveRequested = Hud.Draw(BuildStats());
            _imgui.Render();
        }

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

    /// <summary>Writes the current window contents to <c>--output</c>, or to <c>renders/</c>.</summary>
    private static void SaveRender()
    {
        Vector2D<int> size = _window.FramebufferSize;

        // The dated name is right for a render someone made while using the tool -- nothing is
        // ever overwritten, and the file says what it is. It is wrong for one a script
        // produced: an illustration has to land on the same path every time, or regenerating
        // the manual is a commit rather than a check.
        string name = string.Create(
            CultureInfo.InvariantCulture,
            $"{_sceneName}_{_accumulation.SampleIndex + 1}spp_{DateTime.Now:yyyyMMdd-HHmmss}.png");

        string path = _outputPath ?? Path.Combine(OutputDirectory, name);

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
        if (!_capabilities.IsCompute)
        {
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _accumulation.WriteFramebuffer);
        }

        _shader.Use();

        // Uniforms are set after Use(), never before: glUniform* writes into whichever
        // program is current at that moment.
        _buffers.BindTo(_shader);

        _shader.SetUniform("uCameraPosition", _scene.Scene.Camera.Position);
        _shader.SetUniform("uCameraForward", _rayBasis.Forward);
        _shader.SetUniform("uCameraRight", _rayBasis.Right);
        _shader.SetUniform("uCameraUp", _rayBasis.Up);

        if (_capabilities.IsCompute)
        {
            // Read and written by the same dispatch, at an explicit binding rather than through a
            // texture unit. Not layered, so layer 0 of a plain 2D image.
            _gl.BindImageTexture(
                AccumulationBinding,
                _accumulation.HistoryTexture,
                0,
                false,
                0,
                GLEnum.ReadWrite,
                GLEnum.Rgba32f);
        }
        else
        {
            // Units 0 to 4 carry the scene buffers -- primitives, materials, shapes, and the two
            // instancing adds -- so the history takes the next one.
            _gl.ActiveTexture(TextureUnit.Texture5);
            _gl.BindTexture(TextureTarget.Texture2D, _accumulation.HistoryTexture);
            _shader.SetUniform("uHistory", HistoryUnit);
        }

        _shader.SetUniform("uSampleIndex", _accumulation.SampleIndex);
        _shader.SetUniform("uInvResolution", _invResolution);
        _shader.SetUniform("uMaxBounces", _scene.Scene.Render.MaxBounces);

        // The bound of the BVH walk, and a uniform rather than a constant on purpose: it is what
        // stops the driver unrolling the loop, which is what stops the program growing with the
        // number of placements. Guarded for the same reason as uMarchSteps below.
        if (_scene.InstanceCount > 0)
        {
            _shader.SetUniform("uNodeCount", _scene.NodeCount);
        }

        // Guarded rather than set unconditionally: Shader.SetUniform throws on a name the program
        // does not have, deliberately, so that a typo is loud. Only the distance-field backend
        // declares this one.
        if (_useDistanceField)
        {
            _shader.SetUniform("uMarchSteps", _marchSteps);
        }

        // Transmission and media used to be uniforms set here. They are #define symbols now,
        // fixed when the program was compiled in OnLoad -- see the define list there.
        UploadLights();

        if (_capabilities.IsCompute)
        {
            Vector2D<int> size = _window.FramebufferSize;

            // Rounded up: the shader discards the invocations that land off the edge. The group
            // size is declared in raytrace.glsl and has to agree with WorkgroupSize.
            _gl.DispatchCompute(
                (uint)((size.X + WorkgroupSize - 1) / WorkgroupSize),
                (uint)((size.Y + WorkgroupSize - 1) / WorkgroupSize),
                1);

            // The resolve pass samples what was just written, and the write is not visible to it
            // without this. Leaving it out gives a picture that is mostly right and occasionally
            // a frame stale, which is the worst kind of bug to find later.
            _gl.MemoryBarrier(MemoryBarrierMask.TextureFetchBarrierBit);
            return;
        }

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
        // A reset took every GL object with it, and the context that would be asked to delete
        // them may not answer. There is nothing to release -- the driver already released all of
        // it -- and calling into a dead context to say so is how a clean diagnostic turns back
        // into the silent crash it was written to replace.
        if (_contextLost)
        {
            return;
        }

        if (!_headless)
        {
            _imgui.Dispose();
        }

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
