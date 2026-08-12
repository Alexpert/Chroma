using System.Globalization;
using Silk.NET.OpenGL;

namespace Chroma.Rendering;

/// <summary>
/// Asks the driver whether it has reset underneath us, and explains what to do about it.
/// </summary>
/// <remarks>
/// <para>
/// The failure this exists for: a single frame takes longer than the operating system allows one
/// GPU command to run, so the driver is restarted and every GL object the renderer held stops
/// existing. On Windows that watchdog is called Timeout Detection and Recovery and its budget is
/// about two seconds. The renderer notices nothing, because from its side the call simply
/// returned, and the process then dies with no message at all. That is the one failure mode this
/// project has refused since iteration 1, and it was reachable here.
/// </para>
/// <para>
/// <b>What this can and cannot see.</b> <c>glGetGraphicsResetStatus</c> is core in OpenGL 4.5, so
/// the query is always available on the tier this renderer prefers. Whether it ever answers
/// anything but "no reset" is a different question: a driver is only obliged to report through it
/// when the context was created with the robustness attribute asking to be told, and Silk.NET's
/// <c>ContextFlags</c> exposes no way to ask. So this is a best effort that costs one integer
/// query per frame, and it is deliberately paired with the same explanation being printed when a
/// run ends abnormally, because on this driver the process is more likely to be killed outright
/// than to live long enough to be asked.
/// </para>
/// </remarks>
public static class GraphicsReset
{
    /// <summary>
    /// How long one frame may take before the operating system's watchdog is a real risk.
    /// </summary>
    /// <remarks>
    /// Windows allows a GPU command about two seconds. Half of that is where a warning is worth
    /// printing: close enough that a slightly heavier frame crosses the line, far enough that the
    /// warning is not noise.
    /// </remarks>
    private const double DangerousFrameSeconds = 1.0;

    /// <summary>Core since OpenGL 4.5; below that the query does not exist and must not be called.</summary>
    public static bool IsQueryable(GlCapabilities capabilities) =>
        capabilities.Major > 4 || (capabilities.Major == 4 && capabilities.Minor >= 5);

    /// <summary>
    /// The line printed before the first frame, when the run is on a path known to be slow.
    /// </summary>
    /// <remarks>
    /// This is the only warning that reaches a reader in the worst case, and the worst case is
    /// the common one: a frame long enough to trip the watchdog takes the process down with a
    /// fatal native abort, which no handler in this program can catch and after which nothing
    /// gets to print. So it is said in advance or not at all.
    /// </remarks>
    public static string? Caution(bool distanceField)
    {
        if (!distanceField)
        {
            return null;
        }

        return "note: --sdf is a demonstrator and is several times slower than the default backend.\n"
            + "      If the window never paints, or the program vanishes without a message, one frame\n"
            + "      is taking longer than the operating system lets a GPU command run. Reduce --size\n"
            + "      or --march. See documents/raymarching.md.";
    }

    /// <summary>
    /// The warning printed once the first frame has been timed, when it landed near the watchdog.
    /// </summary>
    /// <remarks>
    /// Worth having beside <see cref="Caution"/> because it is a measurement rather than a guess:
    /// a frame that took a second really is one heavier scene away from taking the process down,
    /// and the reader now knows it before spending an hour finding out.
    /// </remarks>
    public static string? AfterFirstFrame(double seconds, int width, int height)
    {
        if (seconds < DangerousFrameSeconds)
        {
            return null;
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"warning: the first frame took {seconds:F1} s at {width}x{height}. The operating system\n"
            + $"         stops a GPU command at about two seconds and restarts the driver, which ends\n"
            + $"         this program without a message. Halve --size to halve the frame.");
    }

    /// <summary>
    /// Null when the context is healthy, otherwise which kind of reset the driver reports.
    /// </summary>
    public static string? Poll(GL gl)
    {
        GLEnum status = (GLEnum)gl.GetGraphicsResetStatus();

        return status switch
        {
            GLEnum.NoError => null,
            GLEnum.GuiltyContextReset => "this program's own work caused the reset",
            GLEnum.InnocentContextReset => "another program on this GPU caused the reset",
            _ => "the cause is not attributable",
        };
    }

    /// <summary>
    /// What happened, why, and what to change, written for the person who typed the command.
    /// </summary>
    /// <param name="cause">
    /// The driver's attribution from <see cref="Poll"/>, or null when the reset was inferred from
    /// the process failing rather than observed through the query.
    /// </param>
    public static string Explain(
        string? cause,
        int width,
        int height,
        bool distanceField,
        int marchSteps,
        bool batch)
    {
        var text = new System.Text.StringBuilder();

        text.AppendLine(cause is null
            ? "error: the graphics driver appears to have reset while drawing."
            : $"error: the graphics driver reset while drawing ({cause}).");

        text.AppendLine();
        text.AppendLine("  One frame took longer than the operating system allows a single GPU command to");
        text.AppendLine("  run. Windows calls this Timeout Detection and Recovery and gives it about two");
        text.AppendLine("  seconds; when it expires the driver is restarted and every buffer, texture and");
        text.AppendLine("  program this renderer created stops existing.");
        text.AppendLine();
        text.AppendLine("  Nothing is wrong with the scene. It is too expensive to draw at this size in one");
        text.AppendLine("  frame, and frame time is what has to come down. In rough order of effect:");
        text.AppendLine();

        long pixels = (long)width * height;
        int suggestedWidth = Math.Max(160, width / 2);
        int suggestedHeight = Math.Max(90, height / 2);

        text.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"    --size {suggestedWidth}x{suggestedHeight}"
            + $"{new string(' ', Math.Max(1, 18 - $"{suggestedWidth}x{suggestedHeight}".Length))}"
            + $"a quarter of the {pixels:N0} pixels being drawn now; frame"));
        text.AppendLine("                          time falls with them almost exactly");

        if (distanceField)
        {
            int suggested = Math.Max(16, marchSteps / 2);
            text.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"    --march {suggested}"
                + $"{new string(' ', Math.Max(1, 20 - suggested.ToString(CultureInfo.InvariantCulture).Length))}"
                + $"half the {marchSteps} sphere-tracing steps a ray may take. The"));
            text.AppendLine("                          silhouette of anything a ray reaches only by grazing");
            text.AppendLine("                          it -- a ground plane at the horizon -- is what pays");
        }

        if (!batch)
        {
            text.AppendLine("    --samples <n> --headless --output <path>");
            text.AppendLine("                          renders without a visible window and stops on its own.");
            text.AppendLine("                          It draws one frame at a time exactly as this did, so");
            text.AppendLine("                          it is subject to the same limit, but it is the right");
            text.AppendLine("                          way to spend minutes on one image");
        }

        if (distanceField)
        {
            text.AppendLine();
            text.AppendLine("  The distance-field backend is a demonstrator rather than a way to render, and it");
            text.AppendLine("  is several times slower than the default on every scene measured. Dropping");
            text.AppendLine("  --sdf is the other way out. See documents/raymarching.md.");
        }

        return text.ToString().TrimEnd();
    }
}
