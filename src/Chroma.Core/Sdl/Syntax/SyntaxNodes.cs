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

/// <summary>
/// <c>[a, b, c]</c> — a list of values of any kind, nesting included.
/// </summary>
/// <remarks>
/// This is the syntax that used to be a vector literal, unchanged. Only what the elements may
/// be widened: they were numbers, and are now expressions of any kind, so a list of points is
/// a list of points rather than a list of numbers meant to be read in pairs.
/// </remarks>
public sealed record ArrayExpression(SourceSpan Span, IReadOnlyList<Expression> Elements)
    : Expression(Span);

/// <summary>
/// <c>target[index]</c> — one element of an array.
/// </summary>
/// <param name="BracketSpan">
/// The <c>[…]</c> alone, so that a message about the index lands on the index rather than on
/// the whole expression that produced the array.
/// </param>
public sealed record IndexExpression(
    SourceSpan Span,
    Expression Target,
    Expression Index,
    SourceSpan BracketSpan) : Expression(Span);

/// <summary>
/// <c>target.name</c> — a field of a struct, or the <c>length</c> of an array.
/// </summary>
/// <remarks>
/// Postfix and left-associative, so <c>a.b.c</c> and <c>a[0].b</c> chain the way they read.
/// Deliberately not available on a node block: an <c>ObjectExpression</c> is a description
/// that a binder later reads, not a record with keys, and that distinction is the reason
/// <see cref="StructStatement"/> exists at all.
/// </remarks>
public sealed record MemberExpression(
    SourceSpan Span,
    Expression Target,
    string Name,
    SourceSpan NameSpan) : Expression(Span);

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
/// <param name="Target">
/// The module the name is reached through, for <c>materials.stone(tint)</c>, or null for the
/// ordinary <c>stone(tint)</c> that resolves against the scope.
/// </param>
public sealed record CallExpression(
    SourceSpan Span,
    string Name,
    SourceSpan NameSpan,
    IReadOnlyList<Expression> Arguments,
    Expression? Target = null) : Expression(Span);

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
/// <param name="Target">
/// The module the type name is reached through, for <c>shapes.Post { x: 1 }</c>, or null for
/// the ordinary form. A node type is never qualified, since a node name is not a binding.
/// </param>
public sealed record ObjectExpression(
    SourceSpan Span,
    string? TypeName,
    SourceSpan TypeNameSpan,
    IReadOnlyList<Statement> Body,
    Expression? Target = null) : Expression(Span);

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
