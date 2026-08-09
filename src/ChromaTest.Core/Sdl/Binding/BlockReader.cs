using System.Numerics;
using ChromaTest.Core.Sdl.Source;

namespace ChromaTest.Core.Sdl.Binding;

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
    /// A three-component vector. <paramref name="allowScalar"/> broadcasts a lone number
    /// to all three components, which is what lets <c>scale: 2</c> mean uniform scaling.
    /// </summary>
    public Vector3 Vector(string name, Vector3 fallback, bool allowScalar = false)
    {
        BoundField? field = Field(name);
        return field is null ? fallback : ToVector(field.Value, name, fallback, allowScalar);
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

        if (value is VectorValue vector && vector.Components.Count == 3)
        {
            return new Vector3(
                (float)vector.Components[0],
                (float)vector.Components[1],
                (float)vector.Components[2]);
        }

        string expected = allowScalar ? "a vector of 3 components or a number" : "a vector of 3 components";
        Diagnostics.Error(value.Span, $"field '{name}' expects {expected}, found {value.Describe()}");
        return fallback;
    }
}
