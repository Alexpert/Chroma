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
/// <para>
/// <b>Bindings are mutable</b>, as JavaScript's <c>let</c> is. That arrived with the C-style
/// loop, which is a counter that changes: a language with one immutable <c>let</c> and one
/// mutable loop variable would have two rules where there can be one. Note that it does not
/// weaken the rule above — a name may be assigned to, and still may not be declared twice.
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

    /// <summary>
    /// Assigns to an existing binding, in whichever frame declared it. False if there is
    /// none — assignment never creates one.
    /// </summary>
    /// <remarks>
    /// Writing into the declaring frame rather than this one is what makes a loop counter
    /// work: the body runs in a fresh frame per iteration and the counter lives in the
    /// header's, so stepping it from inside the body reaches the same binding every time.
    /// </remarks>
    public bool TrySet(string name, SdlValue value)
    {
        for (Scope? scope = this; scope is not null; scope = scope._parent)
        {
            if (scope._values.ContainsKey(name))
            {
                scope._values[name] = value;
                return true;
            }
        }

        return false;
    }
}
