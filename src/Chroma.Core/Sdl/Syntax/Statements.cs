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
/// <param name="IsPrivate">
/// Whether <c>private</c> keeps this declaration inside the file that made it. Meaningful only
/// at the top level of a file another one imports; anywhere else the frame is never exported,
/// so the marker is accepted and does nothing.
/// </param>
public sealed record LetStatement(
    SourceSpan Span,
    string Name,
    SourceSpan NameSpan,
    Expression Value,
    bool IsPrivate = false) : Statement(Span);

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
    IReadOnlyList<Statement> Body,
    bool IsPrivate = false) : Statement(Span);

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
/// <c>a[0] = value</c> or <c>p.x = value</c> — assignment to part of a value.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Target"/> is an <see cref="IndexExpression"/> or a <see cref="MemberExpression"/>,
/// and the chain under it must bottom out in a name: <c>f(x).y = 3</c> has nothing to assign
/// *to*, and is reported rather than evaluated and discarded.
/// </para>
/// <para>
/// <b>It rebuilds rather than mutates.</b> The evaluator makes a new container along the path
/// and writes the result back to the root binding, so nothing anywhere else can observe the
/// change. That is what keeps <c>let q = p; q.x = 5;</c> from touching <c>p</c>, and it is the
/// rule this language already applies to solids, where referencing a binding twice
/// instantiates it twice.
/// </para>
/// </remarks>
public sealed record PathAssignmentStatement(
    SourceSpan Span,
    Expression Target,
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

/// <summary>One declared field of a <see cref="StructStatement"/>.</summary>
/// <remarks>
/// A name and nothing else. The language has one numeric type and no type names to write, so
/// a field declaration has nothing to say beyond which fields there are — which is still the
/// whole of what a record type buys: a fixed set, checked where an instance is written.
/// </remarks>
public readonly record struct StructField(string Name, SourceSpan Span);

/// <summary>
/// <c>struct Point { x, y }</c> — a record type.
/// </summary>
/// <remarks>
/// It declares a type rather than building a value, and the type is bound to its name like any
/// other declaration. An instance is written with the block syntax that already exists,
/// <c>Point { x: 1, y: 2 }</c>, so the parser needs nothing new for it: that is an
/// <see cref="ObjectExpression"/> whose type name happens to resolve to a struct type.
/// </remarks>
public sealed record StructStatement(
    SourceSpan Span,
    string Name,
    SourceSpan NameSpan,
    IReadOnlyList<StructField> Fields,
    bool IsPrivate = false) : Statement(Span);

/// <summary>
/// <c>import "path";</c> or <c>import "path" as name;</c>.
/// </summary>
/// <param name="Alias">
/// The name the module is reached through, or null for the flat form that lands every export
/// directly in the importing scope.
/// </param>
/// <remarks>
/// The keyword was <c>include</c> until iteration 20, and the word was wrong the whole time:
/// this has never been textual insertion. It resolves against the importing file's directory,
/// refuses a cycle, and runs the file in a scope of its own that cannot see the importer's
/// bindings. Renaming it changed nothing about what it does.
/// </remarks>
public sealed record ImportStatement(
    SourceSpan Span,
    string Path,
    SourceSpan PathSpan,
    string? Alias = null,
    SourceSpan AliasSpan = default) : Statement(Span);

/// <summary>The whole file: a flat sequence of statements.</summary>
public sealed record SceneFile(SourceSpan Span, IReadOnlyList<Statement> Statements)
    : SyntaxNode(Span);
