namespace Chroma.Core.Sdl.Lexing;

public enum TokenKind
{
    /// <summary>A character the lexer could not classify. Already reported.</summary>
    Bad,

    EndOfFile,

    Number,
    Identifier,

    /// <summary>A double-quoted literal, used where a field selects between named forms.</summary>
    String,

    // The reserved words. There was one until iteration 8; control flow needs six more, and
    // they are reserved rather than contextual so that 'for' can never also be a node name.
    Let,
    If,
    Else,
    For,
    In,
    True,
    False,
    Include,

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

    /// <summary>The range in <c>for (i in 0..n)</c>. Not an operator anywhere else.</summary>
    DotDot,

    EqualsEquals,
    BangEquals,
    Less,
    LessEquals,
    Greater,
    GreaterEquals,

    AmpersandAmpersand,
    PipePipe,
    Bang,
}
