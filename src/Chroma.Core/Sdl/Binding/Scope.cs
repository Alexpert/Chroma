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
/// <param name="isBuiltinFrame">
/// True for the one frame the language supplies rather than the file. Its bindings are visible
/// everywhere and writable nowhere — see <see cref="TrySet"/>.
/// </param>
public sealed class Scope(Scope? parent = null, bool isBuiltinFrame = false)
{
    private readonly Dictionary<string, SdlValue> _values = new(StringComparer.Ordinal);
    private readonly Scope? _parent = parent;

    /// <summary>Whether this frame holds the language's own bindings rather than a file's.</summary>
    public bool IsBuiltinFrame { get; } = isBuiltinFrame;

    private readonly HashSet<string> _private = new(StringComparer.Ordinal);

    /// <summary>Bindings made in this frame, not counting enclosing ones.</summary>
    public IReadOnlyDictionary<string, SdlValue> Local => _values;

    /// <summary>
    /// What this frame publishes: its bindings, less the ones marked <c>private</c>.
    /// </summary>
    /// <remarks>
    /// Only ever asked of the outermost frame of an imported file. Any other frame is a block,
    /// a loop iteration or a call, and nothing exports one, so a <c>private</c> written there
    /// is recorded and never consulted.
    /// </remarks>
    public IEnumerable<KeyValuePair<string, SdlValue>> Exports =>
        _values.Where(binding => !_private.Contains(binding.Key));

    /// <summary>Marks a binding of this frame as one that does not leave the file.</summary>
    public void MarkPrivate(string name) => _private.Add(name);

    public Scope Nested() => new(this);

    /// <summary>
    /// Whether this name was supplied by the language rather than declared by the file.
    /// </summary>
    /// <remarks>
    /// Asked by the frame that defined the name rather than by the value's type, so that a
    /// built-in constant such as <c>PI</c> — an ordinary number — is as unwritable as a
    /// built-in function is.
    /// </remarks>
    public bool IsBuiltin(string name)
    {
        for (Scope? scope = this; scope is not null; scope = scope._parent)
        {
            if (scope._values.ContainsKey(name))
            {
                return scope.IsBuiltinFrame;
            }
        }

        return false;
    }

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
            if (!scope._values.ContainsKey(name))
            {
                continue;
            }

            // The built-in frame is shared by the file and everything it includes, and holds
            // the language's own names. The evaluator reports the attempt before reaching here;
            // refusing it again costs one line and means no path can write to it by accident.
            if (scope.IsBuiltinFrame)
            {
                return false;
            }

            scope._values[name] = value;
            return true;
        }

        return false;
    }
}
