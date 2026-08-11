using Chroma.Core.Sdl.Source;
using Chroma.Core.Sdl.Syntax;

namespace Chroma.Core.Sdl.Binding;

/// <summary>
/// The result of evaluating an expression. Six types — a number, a string, a boolean, a
/// vector, an object, or a function — matching the language reference.
/// </summary>
public abstract class SdlValue(SourceSpan span)
{
    public SourceSpan Span { get; } = span;

    /// <summary>Wording used in "expected X, found Y" diagnostics.</summary>
    public abstract string Describe();
}

public sealed class NumberValue(SourceSpan span, double value) : SdlValue(span)
{
    public double Value { get; } = value;

    public override string Describe() => "a number";
}

/// <summary>
/// A double-quoted literal.
/// </summary>
/// <remarks>
/// Strings name a variant rather than carry text: <c>spline: "bezier"</c>. Nothing in the
/// language concatenates or compares them, and no node takes one as free-form content — a
/// field that accepts a string accepts a fixed set of words and reports the set when given
/// anything else.
/// </remarks>
public sealed class StringValue(SourceSpan span, string value) : SdlValue(span)
{
    public string Value { get; } = value;

    public override string Describe() => $"the string \"{Value}\"";
}

/// <summary>
/// <c>true</c> or <c>false</c>.
/// </summary>
/// <remarks>
/// No node takes one as a field, and that is deliberate: booleans exist to be the result of
/// a comparison and the argument of an <c>if</c>. Nothing in the language converts a number
/// to one, so <c>if (count)</c> is an error rather than a shortcut — the only reading a
/// scene file could give it is the wrong one.
/// </remarks>
public sealed class BooleanValue(SourceSpan span, bool value) : SdlValue(span)
{
    public bool Value { get; } = value;

    public override string Describe() => $"the boolean {(Value ? "true" : "false")}";
}

public sealed class VectorValue(SourceSpan span, IReadOnlyList<double> components) : SdlValue(span)
{
    public IReadOnlyList<double> Components { get; } = components;

    public override string Describe() =>
        $"a vector of {Components.Count} component{(Components.Count == 1 ? "" : "s")}";
}

/// <summary>
/// An evaluated block. <see cref="TypeName"/> is null for an anonymous object literal,
/// whose type is supplied by the field it is assigned to.
/// </summary>
/// <remarks>
/// A <c>let</c> stores one of these rather than a built scene object, which is what makes
/// <c>let unit = sphere { ... }</c> produce an independent solid at every use site.
/// </remarks>
public sealed class ObjectValue(
    SourceSpan span,
    string? typeName,
    SourceSpan typeNameSpan,
    IReadOnlyList<BoundEntry> entries) : SdlValue(span)
{
    public string? TypeName { get; } = typeName;

    public SourceSpan TypeNameSpan { get; } = typeNameSpan;

    public IReadOnlyList<BoundEntry> Entries { get; } = entries;

    /// <summary>The <c>let</c> binding this value came from, if any. Display only.</summary>
    public string? SourceName { get; private init; }

    /// <summary>
    /// The innermost loop that produced this object, if a loop did.
    /// </summary>
    /// <remarks>
    /// Innermost, because that is the loop whose count a reader would change first. It is
    /// set on the entries a loop iteration appends and carried onto the bound solid, which
    /// is where the span budget eventually needs it.
    /// </remarks>
    public LoopOrigin? Generator { get; private init; }

    public override string Describe() =>
        TypeName is null ? "an object" : $"a '{TypeName}' object";

    public ObjectValue WithSourceName(string name) =>
        new(Span, TypeName, TypeNameSpan, Entries) { SourceName = name, Generator = Generator };

    public ObjectValue WithGenerator(LoopOrigin generator) =>
        new(Span, TypeName, TypeNameSpan, Entries) { SourceName = SourceName, Generator = generator };
}

/// <summary>
/// A <c>fn</c> declaration, as the value its name is bound to.
/// </summary>
/// <remarks>
/// <para>
/// A function is an ordinary value in an ordinary binding, which is what saves it from
/// needing a namespace of its own: the no-shadowing rule, the frames, and an included
/// fragment exporting its declarations all come from <see cref="Scope"/> unchanged.
/// </para>
/// <para>
/// <see cref="Closure"/> is the scope the declaration sits in, captured live rather than
/// copied. Live is what puts the function's own name in scope inside its body — and so what
/// makes recursion possible, which is why <see cref="Evaluator"/> budgets calls as well as
/// loop iterations.
/// </para>
/// </remarks>
public sealed class FunctionValue(
    SourceSpan span,
    string name,
    IReadOnlyList<Parameter> parameters,
    Expression body,
    Scope closure) : SdlValue(span)
{
    public string Name { get; } = name;

    public IReadOnlyList<Parameter> Parameters { get; } = parameters;

    public Expression Body { get; } = body;

    public Scope Closure { get; } = closure;

    public override string Describe() => $"the function '{Name}'";
}

public abstract class BoundEntry(SourceSpan span)
{
    public SourceSpan Span { get; } = span;
}

public sealed class BoundField(
    SourceSpan span,
    string name,
    SourceSpan nameSpan,
    SdlValue value) : BoundEntry(span)
{
    public string Name { get; } = name;

    public SourceSpan NameSpan { get; } = nameSpan;

    public SdlValue Value { get; } = value;
}

public sealed class BoundChild(SourceSpan span, SdlValue value) : BoundEntry(span)
{
    public SdlValue Value { get; } = value;
}
