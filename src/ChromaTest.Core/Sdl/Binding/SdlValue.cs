using ChromaTest.Core.Sdl.Source;

namespace ChromaTest.Core.Sdl.Binding;

/// <summary>
/// The result of evaluating an expression. Four types only — a number, a string, a vector,
/// or an object — matching the language reference.
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

    public override string Describe() =>
        TypeName is null ? "an object" : $"a '{TypeName}' object";

    public ObjectValue WithSourceName(string name) =>
        new(Span, TypeName, TypeNameSpan, Entries) { SourceName = name };
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
