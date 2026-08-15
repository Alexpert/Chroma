using Chroma.Core.Sdl.Source;

namespace Chroma.Core.Compilation;

/// <summary>
/// What a shape costs the driver, and how much of that there is to spend.
/// </summary>
/// <remarks>
/// <para>
/// NVIDIA's assembly profile refuses a program past roughly 65,000 static instructions, counted
/// after it has inlined every call and unrolled every constant-bound loop. Before instancing that
/// ceiling fell on the number of roots a scene had; it now falls on how much <b>different</b>
/// geometry it holds, which is a much better place for it and still a place a scene can reach.
/// Two things need a number rather than a shrug: the partition, which has to decide what to share
/// before the driver has seen anything, and the refusal, which should name the shapes responsible
/// instead of quoting a line count.
/// </para>
/// <para>
/// The unit is a <b>statement after unrolling</b>, counted by <see cref="Codegen.GlslWriter"/> as
/// the emitter writes. A shape's cost is therefore a property of what it emits, in the same sense
/// that its identity is: there is no second description of a solid here that could drift out of
/// step with the one that generates the code. Adding an eleventh primitive costs it correctly
/// without anything in this file changing.
/// </para>
/// <para>
/// What is <b>not</b> claimed: that a statement is an instruction. One statement becomes anywhere
/// between zero and a dozen, and <c>PUSH</c> is a macro that counts as one and expands to five.
/// The claim is only that the total is proportional to what the driver counts, with the constant
/// folded into <see cref="Budget"/> and measured rather than guessed. See
/// documents/gpu-backends.md for the sweep it was measured with, and its residuals.
/// </para>
/// <para>
/// The driver stays the authority. This is a first guess good enough to partition on, and
/// <c>Program.CompileTracer</c> answers a refusal by lowering the budget and trying again.
/// </para>
/// </remarks>
public static class ShapeCost
{
    /// <summary>
    /// Statements a scene may emit before the driver is likely to refuse it.
    /// </summary>
    /// <remarks>
    /// Calibrated against the sweep in documents/gpu-backends.md: synthetic scenes of N distinct
    /// shapes of one kind, binary-searched for the largest N that compiles. It is deliberately set
    /// a little under the fitted value, because the two sides of being wrong are not symmetric:
    /// sharing a shape that did not need it costs a few percent of a frame, and not sharing one
    /// that did costs the whole scene.
    /// </remarks>
    public const int Budget = 20_000;

    /// <summary>
    /// What reaching one shape costs, over and above evaluating it.
    /// </summary>
    /// <remarks>
    /// A folded shape's bounds test and its resolve call, or a shared shape's <c>switch</c> label
    /// and the same call, counted from <c>GeometryEmitter.WriteTraceScene</c>. The two are within
    /// a statement of each other and both are noise beside a body, so one number serves; it is
    /// here at all because a folded shape pays it once per <i>placement</i>.
    /// </remarks>
    public const int Reach = 8;

    /// <summary>What a cost is as a share of the budget, for saying so out loud.</summary>
    /// <remarks>
    /// Whole percent, and integer arithmetic, so that the number reads the same on a machine whose
    /// culture writes a percentage as <c>1 360 %</c> as on one that writes <c>1360%</c>. The
    /// estimate is not precise enough for a decimal place to mean anything either.
    /// </remarks>
    public static string Share(int cost) => $"{(long)cost * 100 / Math.Max(Budget, 1)}%";
}

/// <summary>
/// One distinct shape, as a refusal has to be able to talk about it.
/// </summary>
/// <param name="Kind">The solid at the top of the shape, by the name the language calls it.</param>
/// <param name="Origin">Where the shape was written, which is where it would be made cheaper.</param>
/// <param name="Generator">The loop that produced it, when one did.</param>
/// <param name="Leaves">How many primitives it is built from.</param>
/// <param name="Cost">What one appearance weighs. See <see cref="ShapeCost"/>.</param>
/// <param name="Placements">How many times it appears in the scene.</param>
/// <param name="Instanced">Whether those appearances share one body.</param>
/// <remarks>
/// This exists because "this scene generates 10,514 lines" is not something anyone can act on,
/// and after instancing it is not even the right quantity: what decides whether a scene compiles
/// is how much <b>different</b> geometry it holds, and a scene is nearly always over budget
/// because of two or three shapes rather than because of all of them.
/// </remarks>
public sealed record ShapeReport(
    string Kind,
    SourceSpan Origin,
    LoopOrigin? Generator,
    int Leaves,
    int Cost,
    int Placements,
    bool Instanced)
{
    /// <summary>What the shape costs the program, counting a folded one at each appearance.</summary>
    public int Total => Instanced ? Cost : Cost * Placements;

    /// <summary>
    /// Where to point, in the format editors and terminals already turn into a location.
    /// </summary>
    /// <param name="scene">
    /// The file being compiled, which a span belongs to unless it names one of its own. The rule
    /// is <see cref="DiagnosticBag"/>'s, so a shape written in an included fragment is named in
    /// that fragment.
    /// </param>
    public string Locate(SourceText scene)
    {
        SourceText source = Origin.Source ?? scene;
        (int line, int column) = source.GetLineColumn(Origin.Start);
        return $"{source.Path}:{line}:{column}";
    }

    /// <summary>One line naming this shape and what it is spending.</summary>
    public string Describe(SourceText scene)
    {
        string leaves = Leaves == 1 ? "1 primitive" : $"{Leaves} primitives";
        string appearances = Placements == 1
            ? "once"
            : Instanced
                ? $"{Placements} times, sharing one body"
                : $"{Placements} times, written out at each";

        string where = Generator is null
            ? Locate(scene)
            : $"{Locate(scene)} ({Generator.Describe()}, {Generator.Count} times)";

        return $"{where}: '{Kind}', {leaves}, {ShapeCost.Share(Total)} of the budget, "
            + $"appearing {appearances}";
    }
}
