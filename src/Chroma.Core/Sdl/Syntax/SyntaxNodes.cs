using Chroma.Core.Sdl.Source;

namespace Chroma.Core.Sdl.Syntax;

// The syntax tree is deliberately domain-agnostic: nothing in this file knows that a
// 'sphere' is a shape or that a 'camera' is special. A node is a name followed by a
// block, and that is all the parser is allowed to understand. Meaning is assigned later,
// in Sdl/Binding, which is what lets the whole language layer be replaced in one piece.

public abstract record SyntaxNode(SourceSpan Span);

public abstract record Expression(SourceSpan Span) : SyntaxNode(Span);

public sealed record NumberExpression(SourceSpan Span, double Value)
    : Expression(Span);

/// <summary>
/// A double-quoted literal. <see cref="Value"/> is the contents, without the quotes.
/// </summary>
public sealed record StringExpression(SourceSpan Span, string Value)
    : Expression(Span);

/// <summary><c>true</c> or <c>false</c>.</summary>
public sealed record BooleanExpression(SourceSpan Span, bool Value)
    : Expression(Span);

public sealed record VectorExpression(SourceSpan Span, IReadOnlyList<Expression> Components)
    : Expression(Span);

public sealed record IdentifierExpression(SourceSpan Span, string Name)
    : Expression(Span);

/// <summary>
/// <c>name(a, b)</c> — a function applied to its arguments.
/// </summary>
/// <remarks>
/// The callee is a name rather than an arbitrary expression, which is what keeps
/// <c>IDENT</c> followed by <c>(</c> the only lookahead a call costs, and matches the one
/// place a function can come from: a <c>fn</c> declaration or a parameter holding one.
/// </remarks>
public sealed record CallExpression(
    SourceSpan Span,
    string Name,
    SourceSpan NameSpan,
    IReadOnlyList<Expression> Arguments) : Expression(Span);

public sealed record UnaryExpression(SourceSpan Span, UnaryOperator Operator, Expression Operand)
    : Expression(Span);

public sealed record BinaryExpression(
    SourceSpan Span,
    BinaryOperator Operator,
    Expression Left,
    Expression Right) : Expression(Span);

/// <summary>
/// <c>cond ? a : b</c> — the ternary, and the only way to choose a value.
/// </summary>
/// <remarks>
/// Both arms are required, which is the whole reason this and <see cref="IfStatement"/> are
/// separate constructs rather than one at two sizes: an expression must produce something
/// whichever way the test goes, and a statement need not.
/// </remarks>
public sealed record ConditionalExpression(
    SourceSpan Span,
    Expression Condition,
    Expression WhenTrue,
    Expression WhenFalse) : Expression(Span);

/// <summary>
/// A block: <c>sphere { ... }</c>, or <c>{ ... }</c> with <see cref="TypeName"/> null for
/// an anonymous literal whose type comes from the field it is assigned to.
/// </summary>
/// <param name="Body">
/// Statements, not entries. Fields and children are two of the statement kinds; the others
/// are the control flow that decides how many of them there are.
/// </param>
public sealed record ObjectExpression(
    SourceSpan Span,
    string? TypeName,
    SourceSpan TypeNameSpan,
    IReadOnlyList<Statement> Body) : Expression(Span);

/// <summary>
/// Stands in for an expression the parser could not read. The diagnostic has already been
/// reported at the point of failure, so later stages skip these silently rather than
/// piling a second complaint onto the same mistake.
/// </summary>
public sealed record MissingExpression(SourceSpan Span) : Expression(Span);

public enum UnaryOperator
{
    Negate,
    Not,

    /// <summary><c>~</c>, the bitwise complement of a whole number.</summary>
    Complement,
}

public enum BinaryOperator
{
    Add,
    Subtract,
    Multiply,
    Divide,

    /// <summary>
    /// <c>%</c>, with C's and JavaScript's sign rule: the result takes the sign of the left
    /// operand, so <c>-1 % 2</c> is <c>-1</c> rather than <c>1</c>.
    /// </summary>
    Modulo,

    Equal,
    NotEqual,
    Less,
    LessOrEqual,
    Greater,
    GreaterOrEqual,

    // Short-circuiting, so the evaluator handles these before it evaluates both sides
    // rather than in the table the arithmetic operators share.
    And,
    Or,

    /// <summary>
    /// <c>&amp;</c>, <c>|</c> and <c>^</c>, which mean what they mean in C: on two booleans
    /// they are the logical connectives without the short circuit, and on two whole numbers
    /// they are the bitwise ones.
    /// </summary>
    /// <remarks>
    /// One operator for both readings rather than two spellings, because the operand kinds
    /// already tell them apart and the language refuses to mix the kinds anyway. <c>^</c> is
    /// the one that had no spelling at all before: exclusive or had to be written
    /// <c>(a || b) &amp;&amp; !(a &amp;&amp; b)</c>.
    /// </remarks>
    BitwiseAnd,
    BitwiseOr,
    BitwiseXor,

    /// <summary><c>&lt;&lt;</c> and <c>&gt;&gt;</c>, on whole numbers only.</summary>
    ShiftLeft,
    ShiftRight,
}
