namespace ChromaTest.Core.Sdl.Binding;

/// <summary>
/// The <c>let</c> bindings of a file: visible from the point of declaration onward, and
/// immutable. Redeclaring a name is rejected by the caller rather than shadowed, because
/// a shadow in a scene file is almost always a typo.
/// </summary>
public sealed class Scope
{
    private readonly Dictionary<string, SdlValue> _values = new(StringComparer.Ordinal);

    public bool Contains(string name) => _values.ContainsKey(name);

    public bool TryGet(string name, out SdlValue value) => _values.TryGetValue(name, out value!);

    public void Define(string name, SdlValue value) => _values[name] = value;
}
