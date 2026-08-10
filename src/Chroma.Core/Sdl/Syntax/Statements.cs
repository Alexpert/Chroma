using Chroma.Core.Sdl.Source;

namespace Chroma.Core.Sdl.Syntax;

public abstract record Statement(SourceSpan Span) : SyntaxNode(Span);

public sealed record LetStatement(
    SourceSpan Span,
    string Name,
    SourceSpan NameSpan,
    Expression Value) : Statement(Span);

public sealed record ExpressionStatement(SourceSpan Span, Expression Value) : Statement(Span);

/// <summary>The whole file: a flat sequence of statements.</summary>
public sealed record SceneFile(SourceSpan Span, IReadOnlyList<Statement> Statements)
    : SyntaxNode(Span);
