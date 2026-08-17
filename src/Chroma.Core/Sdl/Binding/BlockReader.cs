using System.Globalization;
using System.Numerics;
using Chroma.Core.Sdl.Source;

namespace Chroma.Core.Sdl.Binding;

/// <summary>
/// Reads the fields and children of one block on behalf of a binder, tracking which
/// entries were consumed.
/// </summary>
/// <remarks>
/// The tracking is the point. Every binder reads the fields it knows about and
/// <see cref="ReportUnusedEntries"/> then complains about whatever is left, which is how
/// <c>unknown field 'raduis' on 'sphere'</c> is produced without any binder having to
/// enumerate what it does *not* accept. <see cref="BindingContext"/> calls it
/// automatically, so a binder cannot forget.
/// </remarks>
public sealed class BlockReader
{
    private readonly bool[] _used;

    public BlockReader(ObjectValue block, string nodeName, DiagnosticBag diagnostics)
    {
        Block = block;
        NodeName = nodeName;
        Diagnostics = diagnostics;
        _used = new bool[block.Entries.Count];
    }

    public ObjectValue Block { get; }

    public string NodeName { get; }

    public DiagnosticBag Diagnostics { get; }

    public IReadOnlyList<BoundEntry> Entries => Block.Entries;

    public SourceSpan Span => Block.Span;

    /// <summary>Span of the type name, or of the whole block for an anonymous literal.</summary>
    public SourceSpan NameSpan =>
        Block.TypeName is null ? Block.Span : Block.TypeNameSpan;

    public void MarkUsed(int index) => _used[index] = true;

    /// <summary>
    /// The field with this name, marking it consumed. A repeated field is reported and the
    /// first occurrence wins — silently taking the last would hide the mistake.
    /// </summary>
    public BoundField? Field(string name)
    {
        BoundField? found = null;

        for (int i = 0; i < Block.Entries.Count; i++)
        {
            if (Block.Entries[i] is not BoundField field || field.Name != name)
            {
                continue;
            }

            _used[i] = true;

            if (found is null)
            {
                found = field;
            }
            else
            {
                Diagnostics.Error(
                    field.NameSpan,
                    $"field '{name}' is set more than once on '{NodeName}'");
            }
        }

        return found;
    }

    public double Number(string name, double fallback)
    {
        BoundField? field = Field(name);
        if (field is null)
        {
            return fallback;
        }

        if (field.Value is NumberValue number)
        {
            return number.Value;
        }

        Diagnostics.Error(
            field.Value.Span,
            $"field '{name}' expects a number, found {field.Value.Describe()}");
        return fallback;
    }

    public float Single(string name, float fallback) => (float)Number(name, fallback);

    /// <summary>
    /// A whole number within an inclusive range.
    /// </summary>
    /// <remarks>
    /// The language has one numeric type, so "integer" is a constraint rather than a kind.
    /// A fractional value is reported instead of truncated, and an out-of-range one instead
    /// of clamped: both are typing mistakes, and silently accepting them produces a render
    /// that does not match the file.
    /// </remarks>
    public int Integer(string name, int fallback, int min, int max)
    {
        BoundField? field = Field(name);
        if (field is null)
        {
            return fallback;
        }

        if (field.Value is not NumberValue number)
        {
            Diagnostics.Error(
                field.Value.Span,
                $"field '{name}' expects a number, found {field.Value.Describe()}");
            return fallback;
        }

        double value = number.Value;
        string printed = value.ToString("0.###", CultureInfo.InvariantCulture);

        if (value != Math.Floor(value))
        {
            Diagnostics.Error(
                field.Value.Span,
                $"field '{name}' expects a whole number, found {printed}");
            return fallback;
        }

        if (value < min || value > max)
        {
            Diagnostics.Error(
                field.Value.Span,
                $"field '{name}' expects a value between {min} and {max}, found {printed}");
            return fallback;
        }

        return (int)value;
    }

    /// <summary>
    /// A three-component vector. <paramref name="allowScalar"/> broadcasts a lone number
    /// to all three components, which is what lets <c>scale: 2</c> mean uniform scaling.
    /// </summary>
    public Vector3 Vector(string name, Vector3 fallback, bool allowScalar = false)
    {
        BoundField? field = Field(name);
        return field is null ? fallback : ToVector(field.Value, name, fallback, allowScalar);
    }

    /// <summary>
    /// A required string field, or null having reported that it is missing or is not a string.
    /// </summary>
    /// <remarks>
    /// The only field in the language whose value is neither a number nor a name: a path.
    /// <see cref="Keyword"/> also reads a string, but it reads it as one of a closed set, which
    /// is a different question and gives a different diagnostic.
    /// </remarks>
    public string? Text(string name)
    {
        BoundField? field = Field(name);

        if (field is null)
        {
            Diagnostics.Error(NameSpan, $"'{NodeName}' requires '{name}'");
            return null;
        }

        if (field.Value is StringValue text)
        {
            return text.Value;
        }

        Diagnostics.Error(
            field.Value.Span,
            $"field '{name}' expects a string, found {field.Value.Describe()}");

        return null;
    }

    /// <summary>
    /// A boolean field, or <paramref name="fallback"/> when it is absent.
    /// </summary>
    public bool Flag(string name, bool fallback)
    {
        BoundField? field = Field(name);

        if (field is null)
        {
            return fallback;
        }

        if (field.Value is BooleanValue flag)
        {
            return flag.Value;
        }

        Diagnostics.Error(
            field.Value.Span,
            $"field '{name}' expects true or false, found {field.Value.Describe()}");

        return fallback;
    }

    /// <summary>
    /// A string field constrained to a fixed set of words, as an index into
    /// <paramref name="allowed"/>. Returns 0 — the first entry, and so the default — when the
    /// field is absent or unusable.
    /// </summary>
    /// <remarks>
    /// The set is reported back on a mistake, which is the whole reason these fields take a
    /// string rather than a number: <c>spline: "bezier"</c> misspelled says what the choices
    /// are, where <c>spline: 2</c> could only say that 2 is out of range.
    /// </remarks>
    public int Keyword(string name, params string[] allowed)
    {
        BoundField? field = Field(name);

        if (field is null)
        {
            return 0;
        }

        if (field.Value is StringValue text)
        {
            for (int i = 0; i < allowed.Length; i++)
            {
                if (allowed[i] == text.Value)
                {
                    return i;
                }
            }
        }

        string choices = string.Join(", ", allowed.Select(a => $"\"{a}\""));
        Diagnostics.Error(
            field.Value.Span,
            $"field '{name}' expects one of {choices}, found {field.Value.Describe()}");

        return 0;
    }

    /// <summary>
    /// A vector of any length, as its raw components. Returns null when the field is absent
    /// or is not a vector, having reported the latter.
    /// </summary>
    /// <remarks>
    /// Two spellings reach here as one run of numbers: the flat <c>[x0, z0, x1, z1, ...]</c>
    /// the language had before arrays could nest, and the <c>[[x0, z0], [x1, z1], ...]</c> that
    /// says what it is. <paramref name="groupOf"/> is what makes the second readable, and what
    /// lets a mistake in it name the element that is wrong. For a field that may hold several
    /// such runs, see <see cref="ComponentGroups"/>.
    /// </remarks>
    public IReadOnlyList<double>? Components(string name, int groupOf = 0)
    {
        BoundField? field = Field(name);

        if (field is null)
        {
            Diagnostics.Error(NameSpan, $"'{NodeName}' requires a '{name}' field");
            return null;
        }

        if (field.Value is not ArrayValue array)
        {
            Diagnostics.Error(
                field.Value.Span,
                $"field '{name}' expects a vector, found {field.Value.Describe()}");
            return null;
        }

        // Flat, which is what every one of these fields was before arrays could nest and what
        // every scene written so far says.
        if (array.AsNumbers() is { } flat)
        {
            return flat;
        }

        return groupOf > 0
            ? Flatten(array, name, groupOf)
            : Refuse(array, name, "a vector of numbers");
    }

    /// <summary>
    /// The same field as <see cref="Components"/>, but allowing one more level of nesting, so
    /// that it may hold several runs rather than one. Always returns at least one run.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what a prism's second contour is written as, and the whole of what it cost the
    /// language. The two spellings <see cref="Components"/> accepts stay exactly what they were
    /// and come back as a single run; an array whose elements are themselves lists of points is
    /// one run apiece.
    /// </para>
    /// <para>
    /// The three forms are told apart by looking one level down rather than by counting
    /// brackets, which is what keeps them unambiguous: <c>[[0, 0], [1, 0], [0, 1]]</c> is one
    /// contour of three points because its first element is a list of <i>numbers</i>, and
    /// <c>[[[0, 0], [1, 0], [0, 1]]]</c> is a list of one contour because its first element is
    /// a list of <i>points</i>. Nothing has to guess, and a scene written before this existed
    /// reads the same way it always did.
    /// </para>
    /// </remarks>
    public IReadOnlyList<IReadOnlyList<double>>? ComponentGroups(string name, int groupOf)
    {
        BoundField? field = Field(name);

        if (field is null)
        {
            Diagnostics.Error(NameSpan, $"'{NodeName}' requires a '{name}' field");
            return null;
        }

        if (field.Value is not ArrayValue array)
        {
            Diagnostics.Error(
                field.Value.Span,
                $"field '{name}' expects a vector, found {field.Value.Describe()}");
            return null;
        }

        // Flat, and so one run: the spelling every scene written so far uses.
        if (array.AsNumbers() is { } flat)
        {
            return new[] { flat };
        }

        if (array.Count == 0
            || array.Elements[0] is not ArrayValue first
            || first.Count == 0
            || first.Elements[0] is not ArrayValue)
        {
            // One level down is a number, so the whole field is one run of grouped values.
            return Flatten(array, name, groupOf) is { } single ? new[] { single } : null;
        }

        var runs = new List<IReadOnlyList<double>>(array.Count);

        for (int i = 0; i < array.Count; i++)
        {
            if (array.Elements[i] is not ArrayValue group)
            {
                Diagnostics.Error(
                    array.Elements[i].Span,
                    $"field '{name}' holds lists of groups of {groupOf} numbers, "
                    + $"and element {i} is {array.Elements[i].Describe()}");

                return null;
            }

            if (Flatten(group, name, groupOf, i) is not { } run)
            {
                return null;
            }

            runs.Add(run);
        }

        return runs;
    }

    /// <summary>
    /// A rectangular grid of numbers: an array of rows, each row an array of numbers, every row
    /// the same length. Returns null having reported why not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Neither <see cref="Components"/> nor <see cref="ComponentGroups"/> serves this, and the
    /// reason is worth writing down: both take the group size as an argument because a point is
    /// two numbers and a sphere is four, which is a fact about the field. A grid's row length is
    /// a fact about the <i>data</i> and is not known until it has been read, so the shape has to
    /// come out of the value rather than be checked against something the caller already knew.
    /// </para>
    /// <para>
    /// A flat array is refused rather than square-rooted. <c>[0, 1, 2, 3]</c> could be one row or
    /// a two by two, and guessing which would make a typo render instead of reporting.
    /// </para>
    /// </remarks>
    public IReadOnlyList<IReadOnlyList<double>>? Grid(string name)
    {
        BoundField? field = Field(name);

        if (field is null)
        {
            Diagnostics.Error(NameSpan, $"'{NodeName}' requires a '{name}' field");
            return null;
        }

        if (field.Value is not ArrayValue array || array.Count == 0)
        {
            Diagnostics.Error(
                field.Value.Span,
                $"field '{name}' expects an array of rows of numbers, "
                + $"found {field.Value.Describe()}");

            return null;
        }

        var rows = new List<IReadOnlyList<double>>(array.Count);

        for (int j = 0; j < array.Count; j++)
        {
            if (array.Elements[j] is not ArrayValue row)
            {
                Diagnostics.Error(
                    array.Elements[j].Span,
                    $"field '{name}' expects an array of rows of numbers, "
                    + $"and row {j} is {array.Elements[j].Describe()}");

                return null;
            }

            if (row.AsNumbers() is not { } numbers)
            {
                // Named element by element rather than as a whole row, because a grid is written
                // over many lines and "one of these is not a number" is not something to hunt for.
                for (int i = 0; i < row.Count; i++)
                {
                    if (row.Elements[i] is NumberValue)
                    {
                        continue;
                    }

                    Diagnostics.Error(
                        row.Elements[i].Span,
                        $"field '{name}' expects rows of numbers, and row {j} element {i} "
                        + $"is {row.Elements[i].Describe()}");

                    break;
                }

                return null;
            }

            if (rows.Count > 0 && numbers.Count != rows[0].Count)
            {
                Diagnostics.Error(
                    row.Span,
                    $"field '{name}' expects rows of equal length; row {j} has {numbers.Count} "
                    + $"where row 0 has {rows[0].Count}");

                return null;
            }

            rows.Add(numbers);
        }

        return rows;
    }

    /// <summary>
    /// An array of equal-length numeric arrays, flattened into the run the binder reads.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what arrays bought the point-list fields. <c>prism</c> and <c>lathe</c> took
    /// <c>[x0, z0, x1, z1, …]</c> and paired the numbers up themselves, because a vector was
    /// flat and could not hold a point; <c>[[x0, z0], [x1, z1]]</c> now says the same thing and
    /// says what it is. Both spellings are accepted and mean exactly the same run of numbers,
    /// so no scene written against the flat form has to change, and neither does anything
    /// downstream of here — the scene model and its dump see one flattened list either way.
    /// </para>
    /// <para>
    /// Mixing them is refused. An array of arrays with one loose number in it is a typo, and
    /// the group size is the fact that makes the message able to say which element is wrong.
    /// </para>
    /// </remarks>
    /// <param name="run">
    /// Which run of the field this is, for a field that may hold several, so that the message
    /// can say which one. Negative when the field holds exactly one and saying so would only
    /// add a number the reader has to ignore.
    /// </param>
    private IReadOnlyList<double>? Flatten(ArrayValue array, string name, int groupOf, int run = -1)
    {
        string which = run < 0 ? string.Empty : $"list {run + 1}, ";
        List<double> numbers = new(array.Count * groupOf);

        for (int i = 0; i < array.Count; i++)
        {
            if (array.Elements[i] is not ArrayValue group || group.AsNumbers() is not { } values)
            {
                Diagnostics.Error(
                    array.Elements[i].Span,
                    $"field '{name}' expects groups of {groupOf} numbers, "
                    + $"and {which}element {i} is {array.Elements[i].Describe()}");

                return null;
            }

            if (values.Count != groupOf)
            {
                Diagnostics.Error(
                    array.Elements[i].Span,
                    $"field '{name}' expects groups of {groupOf} numbers, "
                    + $"and {which}element {i} has {values.Count}");

                return null;
            }

            numbers.AddRange(values);
        }

        return numbers;
    }

    private IReadOnlyList<double>? Refuse(SdlValue value, string name, string expected)
    {
        Diagnostics.Error(
            value.Span, $"field '{name}' expects {expected}, found {value.Describe()}");

        return null;
    }

    public Vector3 RequireVector(string name, Vector3 fallback)
    {
        BoundField? field = Field(name);

        if (field is null)
        {
            Diagnostics.Error(NameSpan, $"'{NodeName}' requires a '{name}' field");
            return fallback;
        }

        return ToVector(field.Value, name, fallback, allowScalar: false);
    }

    /// <summary>
    /// The child expressions of the block, marking them all consumed. A binder that never
    /// calls this reports its children as unexpected instead.
    /// </summary>
    public IReadOnlyList<SdlValue> Children()
    {
        List<SdlValue> children = [];

        for (int i = 0; i < Block.Entries.Count; i++)
        {
            if (Block.Entries[i] is not BoundChild child)
            {
                continue;
            }

            _used[i] = true;
            children.Add(child.Value);
        }

        return children;
    }

    public void ReportUnusedEntries()
    {
        for (int i = 0; i < Block.Entries.Count; i++)
        {
            if (_used[i])
            {
                continue;
            }

            switch (Block.Entries[i])
            {
                case BoundField field:
                    Diagnostics.Error(
                        field.NameSpan,
                        $"unknown field '{field.Name}' on '{NodeName}'");
                    break;

                case BoundChild child:
                    Diagnostics.Error(
                        child.Span,
                        $"'{NodeName}' does not take child objects");
                    break;
            }
        }
    }

    private Vector3 ToVector(SdlValue value, string name, Vector3 fallback, bool allowScalar)
    {
        if (allowScalar && value is NumberValue scalar)
        {
            float component = (float)scalar.Value;
            return new Vector3(component, component, component);
        }

        if (value is ArrayValue { Count: 3 } array && array.AsNumbers() is { } components)
        {
            return new Vector3(
                (float)components[0], (float)components[1], (float)components[2]);
        }

        string expected = allowScalar ? "a vector of 3 components or a number" : "a vector of 3 components";
        Diagnostics.Error(value.Span, $"field '{name}' expects {expected}, found {value.Describe()}");
        return fallback;
    }
}
