using Chroma.Core.Sdl.Source;
using Chroma.Core.Sdl.Syntax;

namespace Chroma.Core.Sdl.Binding;

/// <summary>
/// The result of evaluating an expression: a number, a string, a boolean, an array, a struct,
/// an object, or a function, matching the language reference. A <c>struct</c> declaration
/// binds an eighth, <see cref="StructTypeValue"/>, which is a type rather than a value of one.
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

/// <summary>
/// <c>[a, b, c]</c> — an ordered list of values of any kind, nesting included.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the language's vector, widened rather than joined by a second kind.</b> A vector
/// was a flat list of numbers that could not nest, which was recorded as a limitation and
/// worked around by interleaving — <c>prism</c> and <c>lathe</c> took
/// <c>[x0, z0, x1, z1, …]</c> and paired the numbers up themselves. Making the elements
/// arbitrary values costs no new syntax and no conversion: an array whose elements are all
/// numbers is exactly the vector that was there before, with the same arithmetic and the same
/// broadcasting.
/// </para>
/// <para>
/// So the two words are one type, and <see cref="Describe"/> picks between them by content: a
/// list of numbers is a <i>vector</i>, because that is what the field taking it wants, and
/// anything else is an <i>array</i>. Every diagnostic written before this existed says the
/// same thing it said.
/// </para>
/// </remarks>
public sealed class ArrayValue(SourceSpan span, IReadOnlyList<SdlValue> elements) : SdlValue(span)
{
    public IReadOnlyList<SdlValue> Elements { get; } = elements;

    public int Count => Elements.Count;

    /// <summary>
    /// The elements as plain numbers, or null if any of them is not one.
    /// </summary>
    /// <remarks>
    /// The question every numeric consumer asks, in one place: arithmetic, a vec3 field and a
    /// point list all need to know whether this array is a vector before they can use it.
    /// </remarks>
    public IReadOnlyList<double>? AsNumbers()
    {
        double[] numbers = new double[Elements.Count];

        for (int i = 0; i < Elements.Count; i++)
        {
            if (Elements[i] is not NumberValue number)
            {
                return null;
            }

            numbers[i] = number.Value;
        }

        return numbers;
    }

    public override string Describe() =>
        AsNumbers() is not null
            ? $"a vector of {Count} component{(Count == 1 ? "" : "s")}"
            : $"an array of {Count} element{(Count == 1 ? "" : "s")}";
}

/// <summary>
/// A <c>struct</c> declaration, as the value its name is bound to.
/// </summary>
/// <remarks>
/// A type rather than a value of that type, and an ordinary binding all the same — which is
/// how <see cref="FunctionValue"/> and <see cref="BuiltinValue"/> already work, and what saves
/// this from needing a namespace of its own. The no-shadowing rule, the frames and an included
/// fragment exporting its declarations all come from <see cref="Scope"/> unchanged.
/// </remarks>
public sealed class StructTypeValue(
    SourceSpan span,
    string name,
    IReadOnlyList<string> fields) : SdlValue(span)
{
    public string Name { get; } = name;

    /// <summary>The declared field names, in declaration order.</summary>
    public IReadOnlyList<string> Fields { get; } = fields;

    public override string Describe() => $"the struct type '{Name}'";
}

/// <summary>
/// An instance of a <see cref="StructTypeValue"/>: <c>Point { x: 1, y: 2 }</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>A record type, not an object literal that happens to have the right keys.</b> The
/// difference is what the declaration buys: the fields are fixed, so a missing one and a
/// misspelt one are both reported at the place they are written, and a struct arriving
/// somewhere can be asked which type it is rather than probed for keys.
/// </para>
/// <para>
/// <b>It is immutable.</b> <c>let</c> became mutable with the C-style loop, and this
/// deliberately did not follow: <c>p.x = 3</c> would have to decide whether a struct is copied
/// or shared when it is passed and assigned, and this language has already decided the
/// analogous question the other way — referencing a binding holding a solid instantiates it
/// twice. A value with no way to be changed has no such question, and building another struct
/// is one line.
/// </para>
/// </remarks>
public sealed class StructValue(
    SourceSpan span,
    StructTypeValue type,
    IReadOnlyDictionary<string, SdlValue> fields) : SdlValue(span)
{
    public StructTypeValue Type { get; } = type;

    public IReadOnlyDictionary<string, SdlValue> Fields { get; } = fields;

    public override string Describe() => $"a '{Type.Name}'";
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
/// A <c>function</c> declaration, as the value its name is bound to.
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
    IReadOnlyList<Statement> body,
    Scope closure) : SdlValue(span)
{
    public string Name { get; } = name;

    public IReadOnlyList<Parameter> Parameters { get; } = parameters;

    public IReadOnlyList<Statement> Body { get; } = body;

    public Scope Closure { get; } = closure;

    public override string Describe() => $"the function '{Name}'";
}

/// <summary>
/// A function the language supplies, as the value its name is bound to.
/// </summary>
/// <remarks>
/// <para>
/// A built-in is an ordinary binding in a frame outside the file, which settles its scoping
/// with no new rule: it is visible everywhere, an included fragment sees the same ones, and
/// the no-shadowing rule refuses a scene's <c>function random(i)</c> rather than letting it
/// quietly win. What that rule needs from this type is only that it be recognisable, so the
/// refusal can name a built-in instead of pointing at a declaration the file does not contain.
/// </para>
/// <para>
/// Numbers in and a number out, which is what every built-in this language has wanted so far.
/// Widening it is a change to this signature and to nothing else.
/// </para>
/// </remarks>
public sealed class BuiltinValue(
    string name,
    IReadOnlyList<BuiltinParameter> parameters,
    Func<BuiltinCall, SdlValue?> apply) : SdlValue(default)
{
    public string Name { get; } = name;

    /// <summary>
    /// The parameters, which fix the arity and what each argument must be.
    /// </summary>
    /// <remarks>
    /// Declared rather than checked in each body, so that <c>'v' of 'normalize' is a vector</c>
    /// is written once and reads the same for every function that wants one.
    /// </remarks>
    public IReadOnlyList<BuiltinParameter> Parameters { get; } = parameters;

    public Func<BuiltinCall, SdlValue?> Apply { get; } = apply;

    public override string Describe() => $"the built-in function '{Name}'";
}

/// <summary>What a built-in's argument has to be.</summary>
/// <remarks>
/// Three kinds, because three is what the library needs: the scalar functions take numbers;
/// <c>length</c>, <c>normalize</c>, <c>dot</c> and <c>cross</c> take an array whose elements
/// are all numbers — which is the language's vector; and <c>array</c> takes whatever it is to
/// repeat, which is <see cref="Any"/> and never reports. A constraint that only one function
/// has, such as <c>cross</c> needing exactly three components, belongs in that function.
/// </remarks>
public enum BuiltinArgument
{
    Number,
    Vector,

    /// <summary>Any value at all, checked by nothing.</summary>
    Any,
}

public readonly record struct BuiltinParameter(string Name, BuiltinArgument Kind = BuiltinArgument.Number);

/// <summary>
/// One call of a built-in, with its arguments already checked against the declared kinds.
/// </summary>
/// <remarks>
/// The accessors are unchecked on purpose: the shared call path has already refused anything
/// that does not match the declaration, so a body that asks for <see cref="Number"/> on a
/// parameter it declared as a number cannot be wrong. What is left for a body to report is
/// its own constraints, through <see cref="Fail"/>.
/// </remarks>
public sealed class BuiltinCall(
    SourceSpan span,
    IReadOnlyList<SdlValue> arguments,
    Action<SourceSpan, string> report)
{
    public SourceSpan Span { get; } = span;

    public IReadOnlyList<SdlValue> Arguments { get; } = arguments;

    public double Number(int index) => ((NumberValue)Arguments[index]).Value;

    public IReadOnlyList<double> Vector(int index) =>
        ((ArrayValue)Arguments[index]).AsNumbers()!;

    public SdlValue Result(double value) => new NumberValue(Span, value);

    public SdlValue Result(IReadOnlyList<double> components) =>
        new ArrayValue(Span, [.. components.Select(c => (SdlValue)new NumberValue(Span, c))]);

    public SdlValue Result(IReadOnlyList<SdlValue> elements) => new ArrayValue(Span, elements);

    /// <summary>Reports a constraint only this function has, and produces no value.</summary>
    public SdlValue? Fail(string message)
    {
        report(Span, message);
        return null;
    }

    /// <summary>Reports against one argument rather than the whole call.</summary>
    public SdlValue? Fail(int index, string message)
    {
        report(Arguments[index].Span, message);
        return null;
    }
}

/// <summary>
/// An imported file, as the value its alias is bound to: <c>import "m.chroma" as m;</c>.
/// </summary>
/// <remarks>
/// <para>
/// It holds what the file exported, which is everything it declared without <c>private</c> in
/// front of it. Reaching a name through <c>m.gold</c> rather than as a bare <c>gold</c> settles
/// the two things the flat form could not: two fragments may both define <c>gold</c>, and the
/// dependency is legible where it is used rather than only at the top of the file.
/// </para>
/// <para>
/// A module is not a struct and deliberately does not pretend to be one. It has no type, no
/// fixed field set and no equality: it is a namespace, and the only thing a file does with one
/// is read a name out of it.
/// </para>
/// </remarks>
public sealed class ModuleValue(
    SourceSpan span,
    string name,
    string path,
    IReadOnlyDictionary<string, SdlValue> exports) : SdlValue(span)
{
    public string Name { get; } = name;

    /// <summary>The file it was read from, for a diagnostic to name.</summary>
    public string Path { get; } = path;

    public IReadOnlyDictionary<string, SdlValue> Exports { get; } = exports;

    public override string Describe() => $"the module '{Name}'";
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
