namespace Chroma.Core.Sdl.Source;

/// <summary>
/// A scene file held in memory, plus the line index needed to turn a character offset
/// back into the line and column a human can act on.
/// </summary>
public sealed class SourceText
{
    // Offset of the first character of each line. Built once; every diagnostic then
    // resolves its position with a binary search rather than by rescanning the file.
    private readonly int[] _lineStarts;

    public SourceText(string path, string text)
    {
        Path = path;
        Text = text;
        _lineStarts = BuildLineStarts(text);
    }

    /// <summary>Path as the user gave it, reproduced verbatim in diagnostics.</summary>
    public string Path { get; }

    public string Text { get; }

    public int Length => Text.Length;

    public char this[int index] => Text[index];

    public static SourceText FromFile(string path) => new(path, File.ReadAllText(path));

    /// <summary>One-based line and column of a character offset.</summary>
    public (int Line, int Column) GetLineColumn(int offset)
    {
        // An offset one past the end is legal: it is where the end-of-file token sits.
        offset = Math.Clamp(offset, 0, Length);

        int index = Array.BinarySearch(_lineStarts, offset);
        if (index < 0)
        {
            // BinarySearch returns the bitwise complement of the next larger element,
            // so the line containing the offset is the one before it.
            index = ~index - 1;
        }

        return (index + 1, offset - _lineStarts[index] + 1);
    }

    public string GetText(SourceSpan span) => Text.Substring(span.Start, span.Length);

    private static int[] BuildLineStarts(string text)
    {
        List<int> starts = [0];

        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                starts.Add(i + 1);
            }
        }

        return [.. starts];
    }
}
