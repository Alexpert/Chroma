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
    Function,
    Return,
    If,
    Else,
    For,
    True,
    False,
    Include,

    /// <summary>
    /// <c>in</c>, which no longer appears in the grammar.
    /// </summary>
    /// <remarks>
    /// Kept reserved for one reason: <c>for (i in 0..n)</c> was the loop form until the
    /// JavaScript revision, every scene and every page of the reference used it, and a file
    /// written against it deserves a diagnostic naming the new form rather than a cascade
    /// about an unexpected identifier. The same goes for <see cref="DotDot"/>.
    /// </remarks>
    In,

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
    Percent,

    /// <summary>
    /// <c>++</c> and <c>--</c>, which step a binding. Statements, not operators: they have no
    /// value, so there is no question of what <c>i++ + 1</c> means.
    /// </summary>
    PlusPlus,
    MinusMinus,

    /// <summary>The <c>?</c> of a ternary. Its <c>:</c> is <see cref="Colon"/>.</summary>
    Question,

    /// <summary>The range of the loop form the JavaScript revision replaced. See <see cref="In"/>.</summary>
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

    /// <summary>
    /// <c>&amp;</c>, <c>|</c> and <c>^</c>, which are C's for both meanings: logical on two
    /// booleans, bitwise on two whole numbers.
    /// </summary>
    /// <remarks>
    /// The first two used to be reported as a near miss for <c>&amp;&amp;</c> and <c>||</c>,
    /// which is what a lone one meant when it could mean nothing else. That reading is gone
    /// now that it is an operator, and only <c>^</c> was ever unspellable.
    /// </remarks>
    Ampersand,
    Pipe,
    Caret,

    /// <summary><c>~</c>, the bitwise complement. Unary, and only on a whole number.</summary>
    Tilde,

    LessLess,
    GreaterGreater,
}
