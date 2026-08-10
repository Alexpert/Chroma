namespace ChromaTest.Core.Compilation;

/// <summary>
/// The worst case a subtree imposes on the shader's fixed-size arrays.
/// </summary>
/// <param name="Spans">
/// Longest span list the subtree can produce. For a leaf this comes from
/// <see cref="GpuLayout.SpansFor"/>: it was always 1 while every primitive was convex, and a
/// torus, a prism, a lathe and a blob are not.
/// </param>
/// <param name="StackDepth">
/// Span lists held at once while evaluating it — the Strahler number of the binarised
/// tree, not its height, so a deep left-leaning chain costs almost nothing.
/// </param>
/// <remarks>
/// GLSL 3.30 has no dynamically sized arrays, so the shader's limits are compile-time
/// constants. Computing the real cost here means an oversized scene is rejected with an
/// explanation instead of silently rendering truncated geometry, which looks exactly like
/// an algorithm bug.
/// </remarks>
public readonly record struct SpanBudget(int Spans, int StackDepth)
{
    public static readonly SpanBudget None = new(0, 0);

    /// <summary>
    /// Combines two operands whose results are both live at the moment of the merge.
    /// </summary>
    public static SpanBudget Combine(SpanBudget left, SpanBudget right, int spans) => new(
        spans,
        Math.Max(
            left.StackDepth,
            // The second operand is evaluated while the first is still on the stack, which
            // is why this is a max of (left, right + 1) rather than a sum.
            right.StackDepth + 1));

    /// <summary>
    /// Worst case for one binary operator.
    /// </summary>
    /// <remarks>
    /// Union can interleave without coalescing anything, so the counts add. Intersection
    /// cannot produce more spans than its thinner operand has. Difference is
    /// <c>A ∩ complement(B)</c>, and complementing <c>B</c> gives <c>|B| + 1</c> intervals,
    /// so the sweep emits at most <c>|A| + |B|</c>.
    /// </remarks>
    public static SpanBudget For(TapeOpcode opcode, SpanBudget left, SpanBudget right) => Combine(
        left,
        right,
        opcode switch
        {
            TapeOpcode.Union => left.Spans + right.Spans,
            TapeOpcode.Intersection => Math.Min(left.Spans, right.Spans),
            _ => left.Spans + right.Spans,
        });
}
