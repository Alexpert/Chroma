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

    /// <summary><c>struct</c>, which declares a record type.</summary>
    Struct,

    /// <summary><c>import "path";</c> and <c>import "path" as name;</c>.</summary>
    Import,

    /// <summary>The <c>as</c> that names an import. Reserved only for that.</summary>
    As,

    /// <summary><c>private</c>, which keeps a declaration inside the file that made it.</summary>
    Private,

    /// <summary>
    /// <c>include</c>, which no longer appears in the grammar.
    /// </summary>
    /// <remarks>
    /// Kept reserved for the same reason as <see cref="In"/>: it was the keyword until
    /// iteration 20, it meant a module rather than textual insertion the whole time, and a
    /// file written against it deserves a diagnostic naming <see cref="Import"/> rather than a
    /// cascade about an unexpected string.
    /// </remarks>
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

    /// <summary>
    /// <c>.</c>, which reads a field of a struct or the length of an array.
    /// </summary>
    /// <remarks>
    /// Lexed after <see cref="DotDot"/>, so <c>0..n</c> still produces the one token that
    /// carries the diagnostic naming the loop form it belonged to rather than two of these.
    /// </remarks>
    Dot,

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
