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

public sealed record UnaryExpression(SourceSpan Span, UnaryOperator Operator, Expression Operand)
    : Expression(Span);

public sealed record BinaryExpression(
    SourceSpan Span,
    BinaryOperator Operator,
    Expression Left,
    Expression Right) : Expression(Span);

/// <summary>
/// <c>if (cond) a else b</c> as a value. The <c>else</c> is required: an expression must
/// produce something whichever way the test goes.
/// </summary>
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
}

public enum BinaryOperator
{
    Add,
    Subtract,
    Multiply,
    Divide,

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
}
