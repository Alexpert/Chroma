using System.Diagnostics;

namespace Chroma.Rendering;

/// <summary>
/// Counts out loud while something before the first image takes its time.
/// </summary>
/// <remarks>
/// <para>
/// A run of <c>scenes/cube.chroma</c> spends over two minutes with a busy CPU, no picture and not
/// one line of output. Almost none of that is this program: reading the scene, canonicalising it
/// and generating its 157,628 lines of GLSL takes about half a second, and the rest is inside
/// <c>glCompileShader</c>. That call blocks the thread that makes it, which is why the count runs
/// on a thread of its own — there is no point in the wait at which anything else could print.
/// </para>
/// <para>
/// It says what is being waited on and for how long, and nothing else. No total afterwards and no
/// per-phase report: what a reader needs is to know the machine is working rather than wedged, and
/// a line that appears and then disappears says that without leaving anything behind to read.
/// </para>
/// <para>
/// Silent for the first second, because almost every scene starts inside it and a renderer that
/// announces a wait that does not happen is noise. That grace is also what keeps the sixty renders
/// of <c>tools/build-manual.ps1</c> looking exactly as they did.
/// </para>
/// </remarks>
public sealed class StartupProgress : IDisposable
{
    /// <summary>How long something may take before it is worth mentioning.</summary>
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(1);

    /// <summary>How often the count is repainted on a terminal.</summary>
    private static readonly TimeSpan Repaint = TimeSpan.FromMilliseconds(250);

    /// <summary>How often it is repeated when there is nothing to repaint onto.</summary>
    /// <remarks>
    /// A redirected stream has no cursor to move, so every update would be a new line and a two
    /// minute compile would be five hundred of them. One line a quarter of a minute is enough to
    /// show in a captured log that the wait was real, and few enough to read.
    /// </remarks>
    private static readonly TimeSpan Repeat = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Written to rather than stdout, which stays the channel a script reads: the scene line, the
    /// <c>benchmark</c> line and the <c>saved</c> line are results, and this is not one.
    /// </summary>
    private static readonly TextWriter Out = Console.Error;

    /// <summary>Whether the count can be repainted in place, or has to be repeated.</summary>
    private static readonly bool Interactive = !Console.IsErrorRedirected;

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _finished = new(false);

    /// <summary>What is being waited on, replaced as it progresses. Read from the other thread.</summary>
    private volatile string _what;

    /// <summary>How wide the last painted line was, so the next one can cover it.</summary>
    private int _painted;

    private StartupProgress(string what)
    {
        _what = what;

        _thread = new Thread(Count)
        {
            IsBackground = true,
            Name = "chroma-progress",
        };

        _thread.Start();
    }

    /// <summary>Starts counting, and prints nothing unless the work outlasts the grace period.</summary>
    public static StartupProgress Begin(string what) => new(what);

    /// <summary>Changes what the count says it is waiting on.</summary>
    /// <remarks>
    /// Deliberately not a percentage. What the driver is doing inside one compile is not
    /// observable, so the only honest progress is which program of how many has come back, and a
    /// bar drawn over an unknown total would be a claim rather than a measurement.
    /// </remarks>
    public void Step(string what) => _what = what;

    /// <summary>Prints a line of its own while the count is running.</summary>
    /// <remarks>
    /// Anything written to the same stream from the thread doing the work would otherwise land in
    /// the middle of a line the counting thread is repainting. There is exactly one such message —
    /// the note the tracer prints between compile attempts — and one shared lock is a great deal
    /// less trouble than one interleaved line nobody can read.
    /// </remarks>
    public void Say(string line)
    {
        lock (_clock)
        {
            Wipe();
            Out.WriteLine(line);
        }
    }

    public void Dispose()
    {
        _finished.Set();
        _thread.Join();

        Erase();
        _finished.Dispose();
    }

    private void Count()
    {
        // Nothing at all until the work has proved it is slow. Waiting on the event rather than
        // sleeping means a fast phase costs one signal and never prints.
        if (_finished.Wait(Grace))
        {
            return;
        }

        TimeSpan period = Interactive ? Repaint : Repeat;

        do
        {
            Paint();
        }
        while (!_finished.Wait(period));
    }

    private void Paint()
    {
        // Whole seconds. This counts a wait measured in minutes, and a flickering decimal would
        // only make it harder to see that the number is still moving.
        string line = $"  {_what}   {_clock.Elapsed.TotalSeconds:F0} s";

        lock (_clock)
        {
            if (!Interactive)
            {
                Out.WriteLine(line);
                return;
            }

            // Padded to cover whatever was there before: a label may shrink -- "9 back" is shorter
            // than "10 programs, 4 back" -- and a shorter line would leave the tail of the old one
            // on screen. The width only ever grows, so what is remembered is the widest line
            // painted rather than the last, and a few trailing spaces are the price.
            Out.Write("\r" + line.PadRight(_painted));
            _painted = Math.Max(_painted, line.Length);
        }
    }

    /// <summary>Takes the count back off the screen, leaving the output as if it had never printed.</summary>
    private void Erase()
    {
        lock (_clock)
        {
            Wipe();
        }
    }

    /// <summary>Clears the painted line. The caller holds the lock.</summary>
    private void Wipe()
    {
        if (!Interactive || _painted == 0)
        {
            return;
        }

        Out.Write("\r" + new string(' ', _painted) + "\r");
        _painted = 0;
    }
}
