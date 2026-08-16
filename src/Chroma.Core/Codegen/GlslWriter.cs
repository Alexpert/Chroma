using System.Globalization;
using System.Numerics;
using System.Text;

namespace Chroma.Core.Codegen;

/// <summary>
/// Builds GLSL source. Nothing here understands geometry; it exists so the emitters can be
/// read as the code they produce rather than as string arithmetic.
/// </summary>
/// <remarks>
/// <para>
/// Output is <b>deterministic</b> — the same scene emits byte-identical source. That is what
/// makes a snapshot test possible, what lets two versions of the emitter be diffed, and what
/// makes a shader cache keyed on the source hash an option later.
/// </para>
/// <para>
/// Every float is written with <see cref="CultureInfo.InvariantCulture"/> and a decimal point,
/// because GLSL has no implicit int-to-float conversion in a constructor argument and a French
/// locale would otherwise emit <c>1,5</c>.
/// </para>
/// </remarks>
internal sealed class GlslWriter
{
    private readonly StringBuilder _text = new(4096);

    /// <summary>Where each open <see cref="Unrolled"/> block sat, and the weight to restore.</summary>
    private readonly Stack<(int Indent, int Weight)> _unrolled = new();

    private int _indent;
    private int _weight = 1;

    /// <summary>
    /// What this text will cost the driver, in statements after unrolling.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The unit is one emitted statement, and the only reason it is a useful number is that
    /// <see cref="Unrolled"/> multiplies by the trip count of a loop the driver will expand. That
    /// is the whole difference between this and a line count, and it is the difference that
    /// matters: documents/gpu-backends.md records a 5,002-line program refused and a 6,436-line
    /// one accepted, because what is counted is instructions after inlining and unrolling.
    /// </para>
    /// <para>
    /// A statement is not an instruction and this does not pretend to be one. It is a quantity
    /// proportional to what the driver counts, with the constant of proportionality measured
    /// rather than guessed — see <see cref="Chroma.Core.Compilation.ShapeCost"/>.
    /// </para>
    /// </remarks>
    public int Cost { get; private set; }

    public GlslWriter Line(string text = "")
    {
        if (text.Length > 0)
        {
            _text.Append(' ', _indent * 4).Append(text);

            if (Counts(text))
            {
                Cost += _weight;
            }
        }

        _text.Append('\n');
        return this;
    }

    /// <summary>Opens a brace-delimited block and indents until <see cref="Close"/>.</summary>
    public GlslWriter Open(string header)
    {
        Line(header);
        Line("{");
        _indent++;
        return this;
    }

    /// <summary>
    /// Opens a loop the driver will <b>unroll</b>, so that everything written inside it counts
    /// <paramref name="trips"/> times towards <see cref="Cost"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only for a loop whose bound is a compile-time constant. A loop bounded by a runtime value
    /// — a span list's <c>count</c>, a crossing count — is compiled once and is opened with
    /// <see cref="Open"/> like anything else. Which of the two a given loop is is not something
    /// this can work out from the header, and getting it wrong is the difference between
    /// estimating a lathe at twenty statements and at five hundred, so it is said explicitly at
    /// the handful of sites where it is true.
    /// </para>
    /// <para>
    /// Nesting composes: a two-iteration loop inside a twenty-four-segment one weighs
    /// forty-eight, which is what the driver will actually write out.
    /// </para>
    /// </remarks>
    public GlslWriter Unrolled(string header, int trips)
    {
        _unrolled.Push((_indent, _weight));
        _weight *= Math.Max(trips, 1);
        return Open(header);
    }

    public GlslWriter Close(string suffix = "")
    {
        _indent--;

        // Matched by depth rather than by a second call, so an Unrolled block cannot be left
        // open by a Close that did not know it was closing one.
        if (_unrolled.Count > 0 && _unrolled.Peek().Indent == _indent)
        {
            _weight = _unrolled.Pop().Weight;
        }

        Line("}" + suffix);
        return this;
    }

    /// <summary>Whether a line is something the driver will emit an instruction for.</summary>
    /// <remarks>
    /// Comments and braces are not, and neither is the punctuation that closes an array
    /// initialiser. An initialiser's <i>elements</i> are counted, one apiece: a
    /// twenty-four-segment lathe carries twenty-four <c>vec4</c> constants and they are part of
    /// what it costs the program.
    /// </remarks>
    private static bool Counts(string text)
    {
        string trimmed = text.TrimStart();

        return !trimmed.StartsWith("//", StringComparison.Ordinal)
            && trimmed is not ("{" or "}" or "};" or ")" or ");");
    }

    /// <summary>
    /// A GLSL float literal that round-trips. <c>R</c> rather than a fixed precision: a
    /// transform baked at load time and the same transform re-derived by hand must agree, and
    /// a truncated literal is a scene that renders subtly displaced with nothing to point at.
    /// </summary>
    public static string Float(float value)
    {
        if (float.IsPositiveInfinity(value)) return "INF";
        if (float.IsNegativeInfinity(value)) return "-INF";
        if (float.IsNaN(value)) return "0.0";

        string text = value.ToString("R", CultureInfo.InvariantCulture);

        // "1" and "1E-05" are both legal C# and neither is a legal GLSL float.
        if (text.Contains('E') || text.Contains('e'))
        {
            return text.Contains('.') ? text : text.Replace("E", ".0E").Replace("e", ".0e");
        }

        return text.Contains('.') ? text : text + ".0";
    }

    public static string Vec2(Vector2 v) => $"vec2({Float(v.X)}, {Float(v.Y)})";

    public static string Vec3(Vector3 v) => $"vec3({Float(v.X)}, {Float(v.Y)}, {Float(v.Z)})";

    public static string Vec4(float x, float y, float z, float w) =>
        $"vec4({Float(x)}, {Float(y)}, {Float(z)}, {Float(w)})";

    /// <summary>
    /// A <c>mat4</c> literal from the rows of a <see cref="Matrix4x4"/>.
    /// </summary>
    /// <remarks>
    /// GLSL's constructor takes <b>columns</b>, so feeding it rows produces the transpose —
    /// which is exactly the column-vector form GLSL needs from a row-vector
    /// <c>System.Numerics</c> matrix. This is the same trick the texture-buffer path used, and
    /// it is the one place in the emitter where the convention lives.
    /// </remarks>
    public static string Mat4(Matrix4x4 m) =>
        "mat4("
        + $"{Float(m.M11)}, {Float(m.M12)}, {Float(m.M13)}, {Float(m.M14)}, "
        + $"{Float(m.M21)}, {Float(m.M22)}, {Float(m.M23)}, {Float(m.M24)}, "
        + $"{Float(m.M31)}, {Float(m.M32)}, {Float(m.M33)}, {Float(m.M34)}, "
        + $"{Float(m.M41)}, {Float(m.M42)}, {Float(m.M43)}, {Float(m.M44)})";

    public override string ToString() => _text.ToString();
}
