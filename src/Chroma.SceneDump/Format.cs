using System.Globalization;
using System.Numerics;

namespace Chroma.SceneDump;

/// <summary>
/// Number formatting for the dump.
/// </summary>
/// <remarks>
/// Every conversion goes through <see cref="CultureInfo.InvariantCulture"/>, for the same
/// reason the lexer does: on a machine whose culture uses a decimal comma, the default
/// formatting would print <c>&lt;0,8 0,2 0,2&gt;</c> — output that no longer reads back as
/// the scene file it came from.
/// </remarks>
internal static class Format
{
    public static string Number(float value) =>
        value.ToString("0.######", CultureInfo.InvariantCulture);

    public static string Vector(Vector3 value) =>
        $"<{Number(value.X)}, {Number(value.Y)}, {Number(value.Z)}>";
}
