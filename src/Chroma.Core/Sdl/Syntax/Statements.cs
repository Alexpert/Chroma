using Chroma.Core.Sdl.Source;

namespace Chroma.Core.Sdl.Syntax;

// A statement is what a scene file is a list of, and it is also what a block is a list of,
// and — since the JavaScript revision — what the body of a function is a list of too.
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

/// <summary>One parameter of a function declaration.</summary>
public readonly record struct Parameter(string Name, SourceSpan Span);

/// <summary>
/// <c>function name(a, b) { … return value; }</c>
/// </summary>
/// <remarks>
/// The body is a statement list and the value comes out through <see cref="ReturnStatement"/>,
/// which is what makes a function a place where work happens rather than a named expression:
/// the statements before the <c>return</c> may bind, branch and loop.
/// </remarks>
public sealed record FunctionStatement(
    SourceSpan Span,
    string Name,
    SourceSpan NameSpan,
    IReadOnlyList<Parameter> Parameters,
    IReadOnlyList<Statement> Body) : Statement(Span);

/// <summary>
/// <c>return value;</c> — the value of the call, and the end of the body.
/// </summary>
/// <remarks>
/// The value is not optional. A function exists to produce one, and a bare <c>return</c>
/// would only ever produce a call site that cannot be used for anything.
/// </remarks>
public sealed record ReturnStatement(
    SourceSpan Span,
    SourceSpan KeywordSpan,
    Expression Value) : Statement(Span);

/// <summary>
/// <c>name = value</c> — assignment to a <c>let</c> binding already in scope.
/// </summary>
/// <remarks>
/// Bindings are mutable because the loop form requires it: <c>for (let i = 0; i &lt; n; i++)</c>
/// is a variable that changes, and a language with one immutable <c>let</c> and one mutable
/// loop counter would have two rules where JavaScript has one.
/// </remarks>
public sealed record AssignmentStatement(
    SourceSpan Span,
    string Name,
    SourceSpan NameSpan,
    Expression Value) : Statement(Span);

/// <summary>
/// <c>name++</c> or <c>name--</c>.
/// </summary>
/// <remarks>
/// A statement rather than an operator, so it has no value and cannot be written inside an
/// expression. That removes every question about evaluation order that C's version raises,
/// and loses nothing: the step clause of a <c>for</c> is the only place it is wanted.
/// </remarks>
public sealed record IncrementStatement(
    SourceSpan Span,
    string Name,
    SourceSpan NameSpan,
    double By) : Statement(Span);

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
/// <c>if (cond) { … } else { … }</c>: emit these entries, or those.
/// </summary>
/// <remarks>
/// <c>if</c> is a statement and only a statement. Choosing between two <i>values</i> is the
/// ternary's job, which is what lets the braces here be mandatory — the reading that used to
/// need settling by position no longer exists.
/// </remarks>
public sealed record IfStatement(
    SourceSpan Span,
    Expression Condition,
    IReadOnlyList<Statement> Then,
    IReadOnlyList<Statement> Else) : Statement(Span);

/// <summary>
/// <c>for (init; condition; step) { … }</c>, as C and JavaScript write it.
/// </summary>
/// <remarks>
/// <para>
/// All three clauses may be empty, so <c>for (;;) { }</c> parses — and this is the one place
/// the language stopped being bounded by construction. A range loop could not run forever;
/// this one can, and what stops it is the evaluator's iteration budget rather than the
/// grammar. That is the trade the JavaScript form costs, made knowingly.
/// </para>
/// </remarks>
/// <param name="KeywordSpan">
/// The <c>for</c> itself. Diagnostics about the loop point here rather than at the whole
/// statement, because the whole statement is the generated geometry.
/// </param>
public sealed record ForStatement(
    SourceSpan Span,
    SourceSpan KeywordSpan,
    Statement? Init,
    Expression? Condition,
    Statement? Step,
    IReadOnlyList<Statement> Body) : Statement(Span);

/// <summary><c>include "path";</c></summary>
public sealed record IncludeStatement(
    SourceSpan Span,
    string Path,
    SourceSpan PathSpan) : Statement(Span);

/// <summary>The whole file: a flat sequence of statements.</summary>
public sealed record SceneFile(SourceSpan Span, IReadOnlyList<Statement> Statements)
    : SyntaxNode(Span);
