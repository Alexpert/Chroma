namespace Chroma.Core.Sdl.Binding;

/// <summary>
/// The <c>let</c> bindings visible at a point in a file: one frame per block, per
/// control-flow body and per loop iteration, chained to the frame that encloses it.
/// </summary>
/// <remarks>
/// <para>
/// A flat dictionary was enough while the binder walked the tree once. A loop body runs
/// many times and its <c>let</c>s must not survive from one iteration to the next, so each
/// iteration gets a frame of its own — which is also what makes the loop variable itself an
/// ordinary binding rather than a special case.
/// </para>
/// <para>
/// <b>Nothing shadows.</b> A name already visible anywhere up the chain cannot be bound
/// again, loop variables included. Shadowing in a scene file is almost always a typo, and
/// the check that caught it in one frame is worth exactly as much across several.
/// </para>
/// </remarks>
public sealed class Scope(Scope? parent = null)
{
    private readonly Dictionary<string, SdlValue> _values = new(StringComparer.Ordinal);
    private readonly Scope? _parent = parent;

    /// <summary>Bindings made in this frame, not counting enclosing ones.</summary>
    public IReadOnlyDictionary<string, SdlValue> Local => _values;

    public Scope Nested() => new(this);

    /// <summary>Whether the name is bound here or anywhere enclosing it.</summary>
    public bool Contains(string name) => TryGet(name, out _);

    public bool TryGet(string name, out SdlValue value)
    {
        for (Scope? scope = this; scope is not null; scope = scope._parent)
        {
            if (scope._values.TryGetValue(name, out value!))
            {
                return true;
            }
        }

        value = null!;
        return false;
    }

    public void Define(string name, SdlValue value) => _values[name] = value;
}
