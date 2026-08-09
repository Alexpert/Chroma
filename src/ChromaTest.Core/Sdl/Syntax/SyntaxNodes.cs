using ChromaTest.Core.Sdl.Source;

namespace ChromaTest.Core.Sdl.Syntax;

// The syntax tree is deliberately domain-agnostic: nothing in this file knows that a
// 'sphere' is a shape or that a 'camera' is special. A node is a name followed by a
// block, and that is all the parser is allowed to understand. Meaning is assigned later,
// in Sdl/Binding, which is what lets the whole language layer be replaced in one piece.

public abstract record SyntaxNode(SourceSpan Span);

public abstract record Expression(SourceSpan Span) : SyntaxNode(Span);

public sealed record NumberExpression(SourceSpan Span, double Value)
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
/// A block: <c>sphere { ... }</c>, or <c>{ ... }</c> with <see cref="TypeName"/> null for
/// an anonymous literal whose type comes from the field it is assigned to.
/// </summary>
public sealed record ObjectExpression(
    SourceSpan Span,
    string? TypeName,
    SourceSpan TypeNameSpan,
    IReadOnlyList<BlockEntry> Entries) : Expression(Span);

public abstract record BlockEntry(SourceSpan Span) : SyntaxNode(Span);

public sealed record FieldEntry(
    SourceSpan Span,
    string Name,
    SourceSpan NameSpan,
    Expression Value) : BlockEntry(Span);

public sealed record ChildEntry(SourceSpan Span, Expression Value) : BlockEntry(Span);

/// <summary>
/// Stands in for an expression the parser could not read. The diagnostic has already been
/// reported at the point of failure, so later stages skip these silently rather than
/// piling a second complaint onto the same mistake.
/// </summary>
public sealed record MissingExpression(SourceSpan Span) : Expression(Span);

public enum UnaryOperator
{
    Negate,
}

public enum BinaryOperator
{
    Add,
    Subtract,
    Multiply,
    Divide,
}
