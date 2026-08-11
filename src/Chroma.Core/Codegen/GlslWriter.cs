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
    private int _indent;

    public GlslWriter Line(string text = "")
    {
        if (text.Length > 0)
        {
            _text.Append(' ', _indent * 4).Append(text);
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

    public GlslWriter Close(string suffix = "")
    {
        _indent--;
        Line("}" + suffix);
        return this;
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
