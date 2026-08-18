using Chroma.Core.Sdl.Source;

namespace Chroma.Core.Sdl.Binding;

/// <summary>
/// The names every scene file can use without declaring them, and the frame they live in.
/// </summary>
/// <remarks>
/// <para>
/// <b>They are bindings, not syntax.</b> Putting them in a frame the file's own scope nests
/// inside is what makes them cost no new rule: lookup finds them last, so nothing local is
/// slowed or shadowed by one; <see cref="Scope"/>'s no-shadowing rule refuses a scene that
/// declares <c>random</c> or <c>PI</c> itself; and an <c>include</c> runs against a frame of
/// its own over the same built-ins, so a fragment sees exactly what a scene file sees. The
/// frame is marked, so nothing assigns to anything in it — which is what lets <c>PI</c> be an
/// ordinary number rather than a function that returns one.
/// </para>
/// <para>
/// <b>The frame is built per load, because the seed is in it.</b> <c>random</c> and
/// <c>perlin</c> are pure functions of their arguments <i>and</i> of the scene's seed, so the
/// seed is captured when the frame is made rather than passed at every call site. Nothing is
/// shared between two loads.
/// </para>
/// <para>
/// <b>Angles are radians here, unconditionally.</b> <c>render { angles: … }</c> says how the
/// two angular <i>fields</i> are written, and it is bound long after these run; <c>sin</c> is
/// mathematics rather than a field, and takes what it takes in every language. <c>PI</c> is
/// what makes that usable, and a scene working in degrees writes <c>sin(a * PI / 180)</c>.
/// </para>
/// </remarks>
public static class Builtins
{
    /// <summary>
    /// A fresh scope for a file to run in, with the built-ins visible in the frame above it.
    /// </summary>
    /// <remarks>
    /// The returned frame is empty and belongs to the file, so <see cref="Scope.Local"/> on it
    /// still holds only what the file declared — which is what <c>include</c> exports.
    /// </remarks>
    public static Scope RootScope(int seed) => BuildFrame(seed).Nested();

    /// <summary>
    /// The names the language supplies, for anything outside it that has to list them.
    /// </summary>
    /// <remarks>
    /// The editor grammar, which colours these as built-ins rather than as names a file
    /// declared, and a test that refuses to let the two lists drift apart. The seed is
    /// irrelevant to a list of names, so any will do.
    /// </remarks>
    public static IReadOnlySet<string> Names { get; } =
        BuildFrame(0).Local.Keys.ToHashSet(StringComparer.Ordinal);

    /// <summary>The frame itself, which <see cref="RootScope"/> nests a file inside.</summary>
    private static Scope BuildFrame(int seed)
    {
        Scope builtins = new(isBuiltinFrame: true);

        builtins.Define("PI", new NumberValue(default, Math.PI));

        Define(builtins, "random", ["i"], call => call.Result(SceneNoise.Random(seed, call.Number(0))));

        Define(
            builtins,
            "perlin",
            ["x", "y"],
            call => call.Result(SceneNoise.Perlin(seed, call.Number(0), call.Number(1))));

        // Trigonometry, in radians. See the note on the class.
        Scalar(builtins, "sin", Math.Sin);
        Scalar(builtins, "cos", Math.Cos);
        Scalar(builtins, "tan", Math.Tan);
        Scalar(builtins, "asin", Math.Asin);
        Scalar(builtins, "acos", Math.Acos);
        Scalar(builtins, "atan", Math.Atan);

        Scalar(builtins, "sqrt", Math.Sqrt);
        Scalar(builtins, "exp", Math.Exp);

        // The natural logarithm, as C's 'log' is. A base-10 or base-2 one would be a second
        // name for 'log(x) / log(10)', which the arithmetic already writes.
        Scalar(builtins, "log", Math.Log);

        Scalar(builtins, "abs", Math.Abs);
        Scalar(builtins, "floor", Math.Floor);
        Scalar(builtins, "ceil", Math.Ceiling);

        // Away from zero at a half, which is C's 'round' and what a reader expects of the word.
        // .NET's default is banker's rounding, and 'round(0.5) == 0' would be a surprise no
        // scene file wants to discover in a coordinate.
        Scalar(builtins, "round", x => Math.Round(x, MidpointRounding.AwayFromZero));

        // Math.Sign throws on NaN rather than answering, which is the one place a one-line
        // wrapper is not enough.
        Scalar(builtins, "sign", x => double.IsNaN(x) ? double.NaN : Math.Sign(x));

        Binary(builtins, "atan2", "y", "x", Math.Atan2);
        Binary(builtins, "pow", "x", "y", Math.Pow);
        Binary(builtins, "min", "a", "b", Math.Min);
        Binary(builtins, "max", "a", "b", Math.Max);

        Define(
            builtins,
            "clamp",
            ["x", "lo", "hi"],
            call => call.Number(1) > call.Number(2)
                ? call.Fail("'clamp' needs 'lo' to be no greater than 'hi'")
                : call.Result(Math.Clamp(call.Number(0), call.Number(1), call.Number(2))));

        DefineArray(builtins);
        DefineVectorFunctions(builtins);

        return builtins;
    }

    /// <summary>
    /// <c>array(n, value)</c> — <c>n</c> copies of one value.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The length an array literal cannot give when the count is a variable. <c>array(5, 0)</c>
    /// is five zeros, which is what a file filling a table by index needs; the second argument
    /// is anything at all, so <c>array(3, [0, 0])</c> is three points and there is no second
    /// name for a version that repeats something other than a number.
    /// </para>
    /// <para>
    /// <b>Every slot holds the same value, and nothing can tell.</b> Values in this language
    /// cannot be changed, and a block is a description instantiated where it is used, so
    /// <c>array(4, sphere { radius: 1 })</c> is four spheres exactly as writing the literal four
    /// times would be.
    /// </para>
    /// </remarks>
    private static void DefineArray(Scope builtins) =>
        Define(
            builtins,
            "array",
            [new BuiltinParameter("n"), new BuiltinParameter("value", BuiltinArgument.Any)],
            call =>
            {
                double count = call.Number(0);
                string written = Evaluator.Printed(count);

                if (!double.IsFinite(count) || count != Math.Floor(count))
                {
                    return call.Fail(0, $"'n' of 'array' must be a whole number, found {written}");
                }

                if (count < 0)
                {
                    return call.Fail(0, $"'n' of 'array' must not be negative, found {written}");
                }

                if (count > int.MaxValue)
                {
                    return call.Fail(0, $"'n' of 'array' is larger than an array can be: {written}");
                }

                SdlValue value = call.Arguments[1];
                SdlValue[] elements = new SdlValue[(int)count];
                Array.Fill(elements, value);

                return call.Result(elements);
            });

    /// <summary>
    /// <c>length</c>, <c>normalize</c>, <c>dot</c> and <c>cross</c>.
    /// </summary>
    /// <remarks>
    /// These are what arrays bought. The value model had no way to hand a vector to a function
    /// or get one back until an array could hold anything, so the last two were recorded as
    /// missing from the language rather than as functions nobody had written yet.
    /// </remarks>
    private static void DefineVectorFunctions(Scope builtins)
    {
        // Any length, because the language's vector is any length: a 2D outline point has a
        // magnitude for the same reason a position does.
        Define(
            builtins,
            "length",
            [new BuiltinParameter("v", BuiltinArgument.Vector)],
            call => call.Result(Math.Sqrt(Dot(call.Vector(0), call.Vector(0)))));

        Define(
            builtins,
            "normalize",
            [new BuiltinParameter("v", BuiltinArgument.Vector)],
            call =>
            {
                IReadOnlyList<double> v = call.Vector(0);
                double length = Math.Sqrt(Dot(v, v));

                // Reported rather than answered with NaN, unlike the domain errors of the
                // scalar functions: a zero vector has no direction at all, so there is no
                // number to return and no reading of the file that wanted one.
                return length == 0.0
                    ? call.Fail(0, "'normalize' has no answer for a vector of length zero")
                    : call.Result([.. v.Select(c => c / length)]);
            });

        Define(
            builtins,
            "dot",
            [new BuiltinParameter("a", BuiltinArgument.Vector), new BuiltinParameter("b", BuiltinArgument.Vector)],
            call => call.Vector(0).Count != call.Vector(1).Count
                ? call.Fail(
                    $"'dot' needs two vectors of the same length, found {call.Vector(0).Count} "
                    + $"and {call.Vector(1).Count}")
                : call.Result(Dot(call.Vector(0), call.Vector(1))));

        Define(
            builtins,
            "cross",
            [new BuiltinParameter("a", BuiltinArgument.Vector), new BuiltinParameter("b", BuiltinArgument.Vector)],
            call =>
            {
                IReadOnlyList<double> a = call.Vector(0);
                IReadOnlyList<double> b = call.Vector(1);

                // Three only. A cross product is defined in three dimensions and in seven, and
                // the seven-dimensional one is not what a scene file means.
                if (a.Count != 3 || b.Count != 3)
                {
                    return call.Fail("'cross' needs two vectors of 3 components");
                }

                return call.Result(
                [
                    (a[1] * b[2]) - (a[2] * b[1]),
                    (a[2] * b[0]) - (a[0] * b[2]),
                    (a[0] * b[1]) - (a[1] * b[0]),
                ]);
            });
    }

    private static double Dot(IReadOnlyList<double> a, IReadOnlyList<double> b)
    {
        double total = 0.0;

        for (int i = 0; i < a.Count; i++)
        {
            total += a[i] * b[i];
        }

        return total;
    }

    private static void Scalar(Scope builtins, string name, Func<double, double> apply) =>
        Define(builtins, name, ["x"], call => call.Result(apply(call.Number(0))));

    private static void Binary(
        Scope builtins,
        string name,
        string first,
        string second,
        Func<double, double, double> apply) =>
        Define(
            builtins,
            name,
            [new BuiltinParameter(first), new BuiltinParameter(second)],
            call => call.Result(apply(call.Number(0), call.Number(1))));

    private static void Define(
        Scope builtins,
        string name,
        IReadOnlyList<BuiltinParameter> parameters,
        Func<BuiltinCall, SdlValue?> apply) =>
        builtins.Define(name, new BuiltinValue(name, parameters, apply));

    /// <summary>Sugar so a list of plain names reads as a list of numeric parameters.</summary>
    private static void Define(
        Scope builtins,
        string name,
        string[] parameters,
        Func<BuiltinCall, SdlValue?> apply) =>
        Define(builtins, name, [.. parameters.Select(p => new BuiltinParameter(p))], apply);
}
