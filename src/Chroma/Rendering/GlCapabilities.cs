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

    private GlCapabilities(
        int major, int minor, GlTier tier, string renderer, string version, bool storageBuffers)
    {
        Major = major;
        Minor = minor;
        Tier = tier;
        Renderer = renderer;
        Version = version;
        UseStorageBuffers = storageBuffers;
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
            tier != GlTier.Fragment33 && !forceTextureBuffers);
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
    /// What to say when a scene will not fit the tier it is being rendered on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Worth doing well: the alternative is a driver log naming an assembly line number in a
    /// program nobody has.
    /// </para>
    /// <para>
    /// It does <b>not</b> suggest a newer OpenGL, because that was measured and does not help.
    /// The same chess set is refused at 65,886 instructions as a fragment shader and at 65,887 as
    /// a compute shader: NVIDIA lowers both stages through the same assembly profile, so the cap
    /// is a property of the driver rather than of the pipeline. The only thing that moves it is
    /// generating less code. See documents/gpu-backends.md.
    /// </para>
    /// </remarks>
    public string ExplainOverflow(int generatedLines, string driverLog)
    {
        string stage = Tier == GlTier.Fragment33 ? "fragment" : "compute";

        string headline =
            $"This scene generates {generatedLines} lines of GLSL, and the driver will not "
            + $"compile that much into one {stage} program: it caps a program at roughly 65,000 "
            + "assembly instructions.\n"
            + "The compute path has the same cap on this driver, so a newer OpenGL does not "
            + "help. What helps is a scene that generates less code -- fewer distinct solids, or "
            + "the same ones written so they can be shared.";

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

        return $"{headline}\nDetected: {Describe()}."
            + (verdict is null ? "" : $"\nDriver: {verdict}");
    }
}
