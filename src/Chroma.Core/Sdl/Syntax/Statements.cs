using Chroma.Core.Sdl.Source;

namespace Chroma.Core.Sdl.Syntax;

// A statement is what a scene file is a list of, and it is also what a block is a list of.
// Those were two hierarchies until iteration 8 — statements at the top level, field/child
// entries inside a block — and control flow is exactly the feature that makes keeping them
// apart expensive: 'if' and 'for' are wanted in both places, and two hierarchies means two
// of each node, two parsers and two evaluators that must not drift.
//
// Unifying them costs one rule instead: a field is only meaningful inside a block, and a
// scene item is only meaningful at the top level. Both are checked where the list is
// consumed, which is where the better message lives anyway.

public abstract record Statement(SourceSpan Span) : SyntaxNode(Span);

/// <summary><c>let name = value;</c></summary>
public sealed record LetStatement(
    SourceSpan Span,
    string Name,
    SourceSpan NameSpan,
    Expression Value) : Statement(Span);

/// <summary><c>name: value</c> — a field of the enclosing block.</summary>
public sealed record FieldStatement(
    SourceSpan Span,
    string Name,
    SourceSpan NameSpan,
    Expression Value) : Statement(Span);

/// <summary>
/// A bare expression: a child of the enclosing block, or a scene item at the top level.
/// </summary>
public sealed record ExpressionStatement(SourceSpan Span, Expression Value) : Statement(Span);

/// <summary>
/// <c>if (cond) … else …</c> at statement level: emit these entries, or those.
/// </summary>
/// <remarks>
/// Distinct from the conditional <i>expression</i>, which chooses between two values. The
/// two are the same feature at two sizes, and the parser tells them apart by position: a
/// <c>{</c> after the condition opens a body here and an object literal there.
/// </remarks>
public sealed record IfStatement(
    SourceSpan Span,
    Expression Condition,
    IReadOnlyList<Statement> Then,
    IReadOnlyList<Statement> Else) : Statement(Span);

/// <summary>
/// <c>for (name in from..to) …</c>, over the whole numbers from <c>from</c> up to but not
/// including <c>to</c>.
/// </summary>
/// <param name="KeywordSpan">
/// The <c>for</c> itself. Diagnostics about the loop point here rather than at the whole
/// statement, because the whole statement is the generated geometry.
/// </param>
public sealed record ForStatement(
    SourceSpan Span,
    SourceSpan KeywordSpan,
    string Variable,
    SourceSpan VariableSpan,
    Expression From,
    Expression To,
    IReadOnlyList<Statement> Body) : Statement(Span);

/// <summary><c>include "path";</c></summary>
public sealed record IncludeStatement(
    SourceSpan Span,
    string Path,
    SourceSpan PathSpan) : Statement(Span);

/// <summary>The whole file: a flat sequence of statements.</summary>
public sealed record SceneFile(SourceSpan Span, IReadOnlyList<Statement> Statements)
    : SyntaxNode(Span);
