namespace Chroma.Core.Compilation;

/// <summary>
/// What walking one subtree tells the compiler: what it costs the shader's fixed arrays, and
/// where in the world it is.
/// </summary>
/// <param name="Budget">The worst case it imposes on the span stack.</param>
/// <param name="Bounds">A box enclosing it, in world space.</param>
/// <remarks>
/// The two travel together because they are produced by the same walk and consumed by the same
/// instruction. Iteration 0 needed only the budget, which is why the visitor returned one; the
/// bound is the second thing a post-order traversal knows about a subtree and nothing else does.
/// </remarks>
internal readonly record struct Subtree(SpanBudget Budget, Aabb Bounds)
{
    public static readonly Subtree None = new(SpanBudget.None, Aabb.Empty);
}
