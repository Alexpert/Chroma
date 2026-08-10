using ChromaTest.Core.Sdl.Source;

namespace ChromaTest.Core.Sdl.Lexing;

/// <summary>
/// One lexical token. Numbers carry their converted value so the parser never has to
/// re-parse text — and so the culture-sensitive conversion happens in exactly one place.
/// </summary>
public readonly record struct Token(
    TokenKind Kind,
    SourceSpan Span,
    string Text,
    double NumberValue = 0.0)
{
    /// <summary>Human-readable name used in "expected X, found Y" diagnostics.</summary>
    public string Describe() => Kind switch
    {
        TokenKind.EndOfFile => "end of file",
        TokenKind.Number => $"number '{Text}'",
        TokenKind.Identifier => $"'{Text}'",
        TokenKind.String => $"string \"{Text}\"",
        TokenKind.Let => "'let'",
        TokenKind.Bad => $"'{Text}'",
        _ => $"'{Text}'",
    };
}
