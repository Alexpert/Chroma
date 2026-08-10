namespace Chroma.Core.Sdl.Source;

/// <summary>
/// A half-open range of characters in a <see cref="SourceText"/>. Every syntax node and
/// every token carries one, so a diagnostic can always point at the exact place in the
/// file that caused it rather than at a line number guessed after the fact.
/// </summary>
/// <param name="Source">
/// The file the offsets index into, or null for the scene file being loaded.
/// </param>
/// <remarks>
/// The file reference is what <c>include</c> costs. Before it, one load meant one file and
/// <see cref="DiagnosticBag"/> could name it once; an included fragment's offsets index a
/// different string entirely, and reporting them against the includer would put every
/// diagnostic on whatever line of the main file happens to share the offset. Null keeps the
/// common case free: a span built without a file — <c>default</c>, or one of the synthetic
/// spans a recovering parser produces — belongs to the file being loaded.
/// </remarks>
public readonly record struct SourceSpan(int Start, int Length, SourceText? Source = null)
{
    public int End => Start + Length;

    /// <summary>
    /// The smallest span covering both operands, which must come from the same file.
    /// </summary>
    /// <remarks>
    /// They always do: this joins a node to its own sub-nodes, and nothing in the language
    /// builds one expression out of two files. The left operand's file wins if they ever
    /// disagree, because it is the one the caller is describing.
    /// </remarks>
    public static SourceSpan Union(SourceSpan left, SourceSpan right)
    {
        int start = Math.Min(left.Start, right.Start);
        int end = Math.Max(left.End, right.End);
        return new SourceSpan(start, end - start, left.Source ?? right.Source);
    }
}
