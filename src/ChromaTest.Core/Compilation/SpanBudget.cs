namespace ChromaTest.Core.Compilation;

/// <summary>
/// The worst case a subtree imposes on the shader's fixed-size arrays.
/// </summary>
/// <param name="Spans">Longest span list the subtree can produce.</param>
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
    /// <summary>A convex primitive: one span, one slot.</summary>
    public static readonly SpanBudget Leaf = new(1, 1);

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
}
