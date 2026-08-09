namespace ChromaTest.Core.Sdl.Source;

/// <summary>
/// A half-open range of characters in a <see cref="SourceText"/>. Every syntax node and
/// every token carries one, so a diagnostic can always point at the exact place in the
/// file that caused it rather than at a line number guessed after the fact.
/// </summary>
public readonly record struct SourceSpan(int Start, int Length)
{
    public int End => Start + Length;

    /// <summary>The smallest span covering both operands.</summary>
    public static SourceSpan Union(SourceSpan left, SourceSpan right)
    {
        int start = Math.Min(left.Start, right.Start);
        int end = Math.Max(left.End, right.End);
        return new SourceSpan(start, end - start);
    }
}
