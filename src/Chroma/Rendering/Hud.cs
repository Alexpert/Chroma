using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using ImGuiNET;

namespace Chroma.Rendering;

/// <summary>What the overlay reports about the frame it is drawn over.</summary>
/// <param name="Width">Framebuffer width in pixels.</param>
/// <param name="Height">Framebuffer height in pixels.</param>
/// <param name="Samples">Samples per pixel accumulated so far.</param>
/// <param name="MaxBounces">Path length limit the scene asked for.</param>
/// <param name="ElapsedSeconds">Time since accumulation last restarted.</param>
/// <param name="FrameMilliseconds">Smoothed cost of one sample per pixel.</param>
/// <param name="RelativeError">
/// Mean relative standard error, or <see cref="float.NaN"/> if not measured yet.
/// </param>
/// <param name="Status">Result of the last save, or null if nothing has been saved.</param>
/// <param name="Update">
/// A release newer than this build, or null while none is known. Read fresh every frame, because
/// the check that finds one answers on a thread of its own and may land mid-render.
/// </param>
internal readonly record struct HudStats(
    int Width,
    int Height,
    int Samples,
    int MaxBounces,
    double ElapsedSeconds,
    double FrameMilliseconds,
    float RelativeError,
    string? Status,
    UpdateNotice? Update);

/// <summary>The stats overlay and its save button.</summary>
internal static class Hud
{
    /// <summary>Relative error at which the convergence bar reads empty.</summary>
    private const float NoiseCeiling = 0.50f;

    /// <summary>Relative error at which it reads full.</summary>
    /// <remarks>
    /// Half a percent is about where noise stops being visible on a screen. There is no such
    /// thing as a finished path trace, so "full" has to be a judgement call rather than a
    /// state the renderer reaches.
    /// </remarks>
    private const float NoiseFloor = 0.005f;

    /// <summary>Where the value column starts, in pixels from the window's left edge.</summary>
    private const float ValueColumn = 116f;

    /// <summary>Width of the convergence bar, sized to span label and value together.</summary>
    private const float BarWidth = 212f;

    private const float Margin = 12f;

    /// <summary>The release link, in the one colour in this overlay that is not grey or white.</summary>
    private static readonly Vector4 LinkColor = new(0.45f, 0.68f, 1f, 1f);

    /// <summary>Applies the overlay's global ImGui settings. Call once, after the controller.</summary>
    internal static unsafe void Configure()
    {
        // Null out the ini path so ImGui neither reads nor writes one. It otherwise drops an
        // imgui.ini into whatever the working directory happens to be -- here, the root of the
        // repository -- to persist a position and size this window sets explicitly anyway.
        ImGui.GetIO().NativePtr->IniFilename = null;
    }

    /// <summary>Draws the overlay for this frame.</summary>
    /// <returns>True on the frame the user clicks Save.</returns>
    internal static bool Draw(in HudStats stats)
    {
        ImGui.SetNextWindowPos(new Vector2(Margin, Margin), ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0.75f);

        // NoSavedSettings keeps ImGui from dropping an imgui.ini next to whatever the working
        // directory happens to be; the window has a fixed position and nothing to remember.
        bool save = false;
        if (ImGui.Begin("Render", ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings))
        {
            Row("resolution", $"{stats.Width} x {stats.Height}");
            Row("samples", $"{stats.Samples} spp");
            Row("bounces", $"{stats.MaxBounces} max");

            ImGui.Separator();

            Row("elapsed", FormatElapsed(stats.ElapsedSeconds));
            Row("frame", Format(stats.FrameMilliseconds, "0.0") + " ms");
            Row("rate", FormatRate(stats.Samples, stats.ElapsedSeconds));

            ImGui.Separator();

            DrawConvergence(stats.RelativeError);

            ImGui.Separator();

            save = ImGui.Button("Save PNG");

            if (stats.Status is not null)
            {
                Muted(stats.Status);
            }

            DrawUpdate(stats.Update);
        }

        ImGui.End();
        return save;
    }

    /// <summary>The one line that says a newer release exists, at the foot of the panel.</summary>
    /// <remarks>
    /// Last, and behind a separator, because it is the only thing in this window that is not about
    /// the picture being rendered. It appears when the check answers, which may be several seconds
    /// into a render, and the panel auto-resizes to hold it.
    /// </remarks>
    private static void DrawUpdate(UpdateNotice? update)
    {
        if (update is not { } release)
        {
            return;
        }

        ImGui.Separator();
        Muted($"version {release.Version} is available");
        Link(release.Url);
    }

    /// <summary>
    /// A URL that opens in the system browser when clicked.
    /// </summary>
    /// <remarks>
    /// ImGui has no hyperlink, so this is one drawn out of the three pieces it does have: coloured
    /// text, the bounding box of the item just emitted, and a draw list to underline it with. The
    /// URL itself was checked before it got here -- see <see cref="UpdateCheck"/> -- which is what
    /// makes handing it to the shell safe, since UseShellExecute launches whatever is registered
    /// for the scheme it carries.
    /// </remarks>
    private static void Link(string url)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, LinkColor);
        ImGui.TextUnformatted(url);
        ImGui.PopStyleColor();

        if (!ImGui.IsItemHovered())
        {
            return;
        }

        ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

        // Underlined on hover only, along the bottom of the text just drawn. Nothing else in this
        // overlay responds to the pointer, so the underline is what says this one does.
        Vector2 min = ImGui.GetItemRectMin();
        Vector2 max = ImGui.GetItemRectMax();
        ImGui.GetWindowDrawList().AddLine(
            new Vector2(min.X, max.Y),
            new Vector2(max.X, max.Y),
            ImGui.GetColorU32(LinkColor));

        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            Open(url);
        }
    }

    /// <summary>Hands the URL to the system browser, and says nothing if it will not go.</summary>
    /// <remarks>
    /// The only time this program launches anything. A machine with no browser registered, or a
    /// desktop session that has no way to open one, is not a rendering problem: the URL is on the
    /// screen either way, and on the console.
    /// </remarks>
    private static void Open(string url)
    {
        try
        {
            using Process? browser = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception)
        {
        }
    }

    private static void DrawConvergence(float relativeError)
    {
        bool measured = !float.IsNaN(relativeError);

        Row("noise", measured ? Format(relativeError * 100.0, "0.00") + " %" : "measuring...");
        ImGui.ProgressBar(Progress(relativeError), new Vector2(BarWidth, 0f), string.Empty);
    }

    /// <summary>How full the convergence bar should be, from a relative error.</summary>
    /// <remarks>
    /// Logarithmic, not linear. Monte Carlo error falls as 1/sqrt(N), so a linear bar spends
    /// almost the whole render pinned at one end and then jumps: going from 20% noise to 10%
    /// costs four times the samples already spent, and from 10% to 5% four times that again.
    /// On a log scale each of those equal-cost steps is an equal step along the bar, which is
    /// what makes it readable as progress.
    /// </remarks>
    private static float Progress(float relativeError)
    {
        if (float.IsNaN(relativeError))
        {
            return 0f;
        }

        if (relativeError <= NoiseFloor)
        {
            return 1f;
        }

        double span = Math.Log(NoiseCeiling / NoiseFloor);
        return (float)Math.Clamp(Math.Log(NoiseCeiling / relativeError) / span, 0.0, 1.0);
    }

    /// <summary>
    /// One label/value line. ImGui's font is proportional, so the value column is placed by
    /// cursor position rather than by padding the label with spaces.
    /// </summary>
    private static void Row(string label, string value)
    {
        Muted(label);
        ImGui.SameLine(ValueColumn);
        ImGui.TextUnformatted(value);
    }

    /// <summary>Dimmed text, for labels and for the save status line.</summary>
    /// <remarks>
    /// Built on TextUnformatted like everything else here: ImGui.Text and ImGui.TextDisabled
    /// take a printf format string, so any '%' in a value silently eats what follows it — a
    /// percentage or a file path is exactly the kind of thing that gets mangled.
    /// </remarks>
    private static void Muted(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
        ImGui.TextUnformatted(text);
        ImGui.PopStyleColor();
    }

    private static string FormatElapsed(double seconds) =>
        TimeSpan.FromSeconds(seconds).ToString(@"hh\:mm\:ss\.ff", CultureInfo.InvariantCulture);

    private static string FormatRate(int samples, double elapsedSeconds) =>
        elapsedSeconds > 0
            ? Format(samples / elapsedSeconds, "0.0") + " spp/s"
            : "--";

    // Invariant throughout: the overlay is written in English, and a decimal separator that
    // changes with the machine's locale would make two screenshots disagree.
    private static string Format(double value, string format) =>
        value.ToString(format, CultureInfo.InvariantCulture);
}
