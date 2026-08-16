using System.Runtime.InteropServices;
using System.Text;
using Chroma.Core.Compilation;
using Silk.NET.OpenGL;

namespace Chroma.Rendering;

/// <summary>Which shader path the machine can run the tracer on.</summary>
public enum GlTier
{
    /// <summary>OpenGL 3.3: a fragment shader over texture buffers, and a fullscreen quad.</summary>
    Fragment33,

    /// <summary>OpenGL 4.3: a compute shader over shader storage buffers, dispatched.</summary>
    Compute43,

    /// <summary>OpenGL 4.6: as 4.3, and SPIR-V can be handed to the driver directly.</summary>
    Compute46,
}

/// <summary>
/// What the context that actually arrived can do.
/// </summary>
/// <remarks>
/// <para>
/// The renderer asked for OpenGL 3.3 from iteration 0 until now, and that was the right floor
/// while the shader was one hand-written file. Generating the geometry per scene changed what the
/// version costs: a chess set's worth of generated code is refused by the GL 3.3 fragment pipeline
/// with <c>error: too many instructions</c>, and the newer paths through the driver may not have
/// that ceiling. So the version became a tier rather than a target.
/// </para>
/// <para>
/// A requested version is a floor, not a request for exactly that: drivers routinely return a
/// newer context than asked for. What matters is what came back, which is why this reads
/// <c>GL_MAJOR_VERSION</c> rather than trusting what was asked.
/// </para>
/// </remarks>
public sealed class GlCapabilities
{
    /// <summary>What to ask the window system for. Anything older still works.</summary>
    public const int PreferredMajor = 4;

    public const int PreferredMinor = 6;

    /// <summary>What to ask for on macOS, where the line above is not a floor but a refusal.</summary>
    /// <remarks>
    /// <para>
    /// Everywhere else a requested version is a floor: the driver is free to return something
    /// newer, and <see cref="Detect"/> reads back what arrived. Apple does not work that way. It
    /// caps OpenGL at <b>4.1</b>, hands out 3.2 and above only through a <b>forward-compatible</b>
    /// core profile, and deprecated the whole API in 2018 — so asking for 4.6 there does not fall
    /// back to 4.1, it fails to create the context and no window ever opens.
    /// </para>
    /// <para>
    /// 3.3 is the floor this renderer targeted for twelve iterations and still renders every
    /// scene that fits the driver's instruction budget. What is given up on a Mac is the compute
    /// tier, which needs 4.3 and is therefore unreachable there for good: <see cref="Detect"/>
    /// already resolves anything below 4.3 to <see cref="GlTier.Fragment33"/>, so
    /// <c>--compute</c> quietly renders on the fragment path instead of failing.
    /// </para>
    /// </remarks>
    public const int MacMajor = 3;

    public const int MacMinor = 3;

    private GlCapabilities(
        int major,
        int minor,
        GlTier tier,
        string renderer,
        string version,
        bool storageBuffers,
        bool parallelCompile)
    {
        Major = major;
        Minor = minor;
        Tier = tier;
        Renderer = renderer;
        Version = version;
        UseStorageBuffers = storageBuffers;
        ParallelCompile = parallelCompile;
    }

    public int Major { get; }

    public int Minor { get; }

    public GlTier Tier { get; }

    /// <summary>The GPU, as the driver names it.</summary>
    public string Renderer { get; }

    /// <summary>The driver's own version string, which carries its build number.</summary>
    public string Version { get; }

    /// <summary>True when the tracer runs as a compute shader rather than over a quad.</summary>
    public bool IsCompute => Tier != GlTier.Fragment33;

    /// <summary>True when the scene tables are storage buffers rather than texture buffers.</summary>
    /// <remarks>Only ever true on the compute tier: OpenGL 3.3 has no storage buffers.</remarks>
    public bool UseStorageBuffers { get; }

    /// <summary>
    /// Whether the driver will compile several programs at once, and can be asked how it is going.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>GL_ARB_parallel_shader_compile</c>, or the identical <c>GL_KHR_</c> spelling. It buys two
    /// things and the first one is the one that matters: the driver actually uses threads, which on
    /// a scene split into ten chunks took its cold compile from 11.1 s to 3.6 s. The second is
    /// <c>GL_COMPLETION_STATUS_ARB</c>, the only way to ask a program how it is getting on without
    /// waiting for it, which is what lets the count on screen say how many have come back.
    /// </para>
    /// <para>
    /// Not in <see cref="Describe"/>. It changes nothing a reader chooses and the line is long
    /// enough.
    /// </para>
    /// </remarks>
    public bool ParallelCompile { get; }

    /// <summary>The <c>#version</c> the tracer is compiled at on this tier.</summary>
    public string GlslVersion => Tier switch
    {
        GlTier.Compute46 => "460 core",
        GlTier.Compute43 => "430 core",
        _ => "330 core",
    };

    /// <param name="useCompute">
    /// <c>--compute</c>. Opt-in rather than automatic: the fragment path is the measured default,
    /// and having both selectable on one GPU is what makes them comparable at all.
    /// </param>
    /// <param name="forceTextureBuffers">
    /// <c>--tbo</c>. Reads the scene tables through a sampler on the compute path too, which is
    /// the A/B for whether storage buffers are actually the faster way to read them.
    /// </param>
    public static GlCapabilities Detect(GL gl, bool useCompute, bool forceTextureBuffers = false)
    {
        gl.GetInteger(GetPName.MajorVersion, out int major);
        gl.GetInteger(GetPName.MinorVersion, out int minor);

        string renderer = gl.GetStringS(StringName.Renderer) ?? "unknown";
        string version = gl.GetStringS(StringName.Version) ?? "unknown";

        int number = (major * 10) + minor;

        GlTier tier = !useCompute ? GlTier.Fragment33
            : number >= 46 ? GlTier.Compute46
            : number >= 43 ? GlTier.Compute43
            : GlTier.Fragment33;

        return new GlCapabilities(
            major,
            minor,
            tier,
            renderer,
            version,
            tier != GlTier.Fragment33 && !forceTextureBuffers,
            TryEnableParallelCompile(gl));
    }

    /// <summary>
    /// Finds the parallel-compile extension and, having found it, turns it on.
    /// </summary>
    /// <remarks>
    /// Extensions are enumerated rather than read as one string: <c>glGetString(GL_EXTENSIONS)</c>
    /// is not allowed in a core profile and returns null there, which would answer "absent" on
    /// every machine that has it.
    /// </remarks>
    private static bool TryEnableParallelCompile(GL gl)
    {
        gl.GetInteger(GetPName.NumExtensions, out int count);

        for (uint i = 0; i < count; i++)
        {
            string? name = gl.GetStringS(StringName.Extensions, i);

            if (name is "GL_ARB_parallel_shader_compile" or "GL_KHR_parallel_shader_compile")
            {
                return AskForCompilerThreads(gl);
            }
        }

        return false;
    }

    private delegate void MaxShaderCompilerThreads(uint count);

    /// <summary>
    /// Asks the driver to use as many threads as the machine has to compile with.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This call is the entire feature, and that was a surprise. The extension states that
    /// <c>MAX_SHADER_COMPILER_THREADS_ARB</c> starts at the implementation maximum, which reads as
    /// though the driver is already using every thread it has and this only lowers the number.
    /// It is not: with the extension present, the completion query working, and every program
    /// handed over before any of them was asked about, a ten-chunk scene's fifteen programs still
    /// compiled strictly end to end and took 11.1 s. The only change that moved it was this line,
    /// and it moved it to 3.6 s.
    /// </para>
    /// <para>
    /// Reached through the context rather than a Silk.NET extension binding: one entry point is not
    /// worth another package reference. Returning false when it is unreachable rather than claiming
    /// the capability, because without it the promise this property makes does not hold.
    /// </para>
    /// </remarks>
    private static bool AskForCompilerThreads(GL gl)
    {
        if (!gl.Context.TryGetProcAddress("glMaxShaderCompilerThreadsARB", out nint address)
            && !gl.Context.TryGetProcAddress("glMaxShaderCompilerThreadsKHR", out address))
        {
            return false;
        }

        Marshal.GetDelegateForFunctionPointer<MaxShaderCompilerThreads>(address)
            ((uint)Environment.ProcessorCount);

        return true;
    }

    /// <summary>How the scene tables are being read, for the console line.</summary>
    private string Tables => UseStorageBuffers ? "storage buffers" : "texture buffers";

    /// <summary>The console line: what was found, and what it is being used for.</summary>
    public string Describe()
    {
        string path = Tier switch
        {
            GlTier.Compute46 => $"compute shader, {Tables}, SPIR-V available",
            GlTier.Compute43 => $"compute shader, {Tables}",
            _ => "fragment shader, texture buffers",
        };

        return $"OpenGL {Major}.{Minor} on {Renderer} -- {path}";
    }

    /// <summary>
    /// Whether a driver log is the program being too large, in any of the ways it says so.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It says so in three ways, and which one arrives is not a property of how far over the
    /// scene is so much as of what it is made of.
    /// </para>
    /// <list type="bullet">
    /// <item><c>too many instructions</c> against an assembly line number: the tidy one, and what
    /// a chess set produced.</item>
    /// <item><c>error C5041: cannot locate suitable resource to bind variable</c>, one line per
    /// temporary it gave up on. The register ceiling rather than the instruction one — the same
    /// failure iteration 7 met from the other side, when every span list was a local. A scene of
    /// two hundred small distinct solids reaches this one while a scene of a hundred larger ones
    /// reaches the first.</item>
    /// <item><c>fatal error C9999: *** exception during compilation ***</c>, which
    /// <c>scenes/cube.chroma</c> produces at some twenty times the budget: past a point the
    /// compiler stops reporting a limit and falls over instead.</item>
    /// </list>
    /// <para>
    /// All three are one program too large for one driver, and telling an author they are
    /// different problems would be telling them something untrue. C5041 was missing here until
    /// the calibration sweep produced one, which meant such a scene skipped the retry and showed
    /// the reader two hundred lines of driver log.
    /// </para>
    /// </remarks>
    public static bool IsOverflow(string driverLog) =>
        driverLog.Contains("too many instructions", StringComparison.OrdinalIgnoreCase)
        || driverLog.Contains("C5041", StringComparison.Ordinal)
        || driverLog.Contains("C9999", StringComparison.Ordinal)
        || driverLog.Contains("exception during compilation", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// What to say when a scene will not fit the tier it is being rendered on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Worth doing well: the alternative is a driver log naming an assembly line number in a
    /// program nobody has.
    /// </para>
    /// <para>
    /// It names the <b>shapes</b>, because after instancing that is what the limit falls on. The
    /// program holds one body per distinct shape and a placement of a shape already emitted costs
    /// a record in a buffer, so a board of ten thousand pieces is the same size of shader as a
    /// board of thirty-two, and "fewer solids" stopped being the useful advice. A scene is over
    /// budget because of two or three expensive shapes far more often than because of all of
    /// them, and those two or three are what this points at.
    /// </para>
    /// <para>
    /// It does <b>not</b> suggest a newer OpenGL, because that was measured and does not help.
    /// The same chess set is refused at 65,886 instructions as a fragment shader and at 65,887 as
    /// a compute shader: NVIDIA lowers both stages through the same assembly profile, so the cap
    /// is a property of the driver rather than of the pipeline. The only thing that moves it is
    /// generating less code. See documents/gpu-backends.md.
    /// </para>
    /// </remarks>
    public string ExplainOverflow(CompiledScene scene, string driverLog)
    {
        string stage = Tier == GlTier.Fragment33 ? "fragment" : "compute";

        var message = new StringBuilder();

        // Which ceiling it was. The advice below is the same either way -- less distinct geometry
        // -- but naming the wrong limit would send a reader looking for the wrong thing, and the
        // two are reached by noticeably different scenes.
        bool registers = driverLog.Contains("C5041", StringComparison.Ordinal);

        message.Append(
            registers
                ? $"This scene is too large for one {stage} program: the driver ran out of "
                    + "registers to hold it, having inlined every call and unrolled every "
                    + "constant-bound loop.\n"
                : $"This scene is too large for one {stage} program: the driver caps a program at "
                    + "roughly 65,000 assembly instructions, counted after it has inlined every "
                    + "call and unrolled every constant-bound loop.\n");

        string shapes = scene.ShapeCount == 1 ? "one distinct shape" : $"{scene.ShapeCount} distinct shapes";

        message.Append(
            $"It holds {shapes}, estimated at {ShapeCost.Share(scene.EstimatedCost)} of what will "
            + "fit. What costs the most:\n");

        // Three. Enough to show whether the scene has one runaway shape or a broad problem, few
        // enough that the list is read rather than skimmed.
        foreach (ShapeReport shape in scene.ShapeReports
            .OrderByDescending(shape => shape.Total)
            .Take(3))
        {
            message.Append($"  {shape.Describe(scene.Source)}\n");
        }

        message.Append(
            "What helps is fewer shapes that are different from each other: a shape that repeats "
            + "is nearly free however often it appears, and one written with fewer segments, "
            + "fewer components or fewer points is cheaper everywhere it stands.\n"
            + "A newer OpenGL does not help. The compute path has the same cap on this driver.\n");

        message.Append($"Detected: {Describe()}.");

        // The driver's own verdict, one line of it. Not the whole log -- a refused program brings
        // back hundreds of lines of assembly listing -- but not nothing either: which limit was
        // hit is the only part of that log anyone can act on.
        string[] lines = [.. driverLog.Split('\n').Select(line => line.Trim())];

        // "line 65886, column 1:  error: too many instructions" is the one that says which limit
        // was hit. It is preferred over the "Internal error: assembly compile error" that precedes
        // it, which says only that something went wrong.
        string? verdict =
            lines.FirstOrDefault(line => line.StartsWith("line ", StringComparison.Ordinal)
                && line.Contains("error", StringComparison.OrdinalIgnoreCase))
            ?? lines.FirstOrDefault(line => line.Contains("error", StringComparison.OrdinalIgnoreCase));

        if (verdict is not null)
        {
            message.Append($"\nDriver: {verdict}");
        }

        return message.ToString();
    }
}
