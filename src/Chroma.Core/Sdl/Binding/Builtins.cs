namespace Chroma.Core.Sdl.Binding;

/// <summary>
/// The functions every scene file can call without declaring them, and the outermost frame
/// they live in.
/// </summary>
/// <remarks>
/// <para>
/// <b>They are bindings, not syntax.</b> Putting them in a frame the file's own scope nests
/// inside is what makes them cost no new rule: lookup finds them last, so nothing local is
/// slowed or shadowed by one; <see cref="Scope"/>'s no-shadowing rule refuses a scene that
/// declares <c>random</c> itself; and an <c>include</c> runs against a frame of its own over
/// the same built-ins, so a fragment sees exactly what a scene file sees.
/// </para>
/// <para>
/// <b>The frame is built per load, because the seed is in it.</b> Both functions here are
/// pure functions of their arguments <i>and</i> of the scene's seed, so the seed is captured
/// when the frame is made rather than passed at every call site. That also means nothing is
/// shared between two loads, which is what keeps a load from being able to affect the next.
/// </para>
/// </remarks>
public static class Builtins
{
    /// <summary>
    /// A fresh scope for a file to run in, with the built-ins visible in the frame above it.
    /// </summary>
    /// <remarks>
    /// The returned frame is empty and belongs to the file, so <see cref="Scope.Local"/> on
    /// it still holds only what the file declared — which is what <c>include</c> exports.
    /// </remarks>
    public static Scope RootScope(int seed)
    {
        Scope builtins = new();

        builtins.Define(
            "random",
            new BuiltinValue("random", ["i"], arguments => SceneNoise.Random(seed, arguments[0])));

        builtins.Define(
            "perlin",
            new BuiltinValue(
                "perlin",
                ["x", "y"],
                arguments => SceneNoise.Perlin(seed, arguments[0], arguments[1])));

        return builtins.Nested();
    }
}
