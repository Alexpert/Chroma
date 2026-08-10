namespace ChromaTest.Core.Sdl.Lexing;

public enum TokenKind
{
    /// <summary>A character the lexer could not classify. Already reported.</summary>
    Bad,

    EndOfFile,

    Number,
    Identifier,

    /// <summary>A double-quoted literal, used where a field selects between named forms.</summary>
    String,

    /// <summary>The one and only reserved word.</summary>
    Let,

    LeftBrace,
    RightBrace,
    LeftBracket,
    RightBracket,
    LeftParen,
    RightParen,

    Colon,
    Comma,
    Semicolon,
    Equals,

    Plus,
    Minus,
    Star,
    Slash,
}
