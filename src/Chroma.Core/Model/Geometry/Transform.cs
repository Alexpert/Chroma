using System.Numerics;

namespace Chroma.Core.Model.Geometry;

public enum TransformKind
{
    Translate,
    Rotate,
    Scale,
}

/// <summary>One <c>translate</c>, <c>rotate</c> or <c>scale</c> modifier, as written.</summary>
public readonly record struct TransformStep(TransformKind Kind, Vector3 Value);

/// <summary>
/// The transform attached to a solid, kept in both of the forms the project needs: the
/// ordered list of steps the author wrote, and the single matrix they compose to.
/// </summary>
/// <remarks>
/// Keeping the steps is not redundancy. The hierarchy dump has to show what the file says,
/// not a matrix nobody can read back; and order is semantic here — translating then
/// rotating does not give the same solid as rotating then translating.
/// </remarks>
public sealed class Transform
{
    public static readonly Transform Identity = new([]);

    public Transform(IReadOnlyList<TransformStep> steps)
    {
        Steps = steps;
        Matrix = Compose(steps);
    }

    public IReadOnlyList<TransformStep> Steps { get; }

    public Matrix4x4 Matrix { get; }

    public bool IsIdentity => Steps.Count == 0;

    private static Matrix4x4 Compose(IReadOnlyList<TransformStep> steps)
    {
        Matrix4x4 result = Matrix4x4.Identity;

        foreach (TransformStep step in steps)
        {
            // System.Numerics is row-vector (v' = v * M), so applying A and then B is
            // A * B. Multiplying on the right therefore matches the written order.
            result *= ToMatrix(step);
        }

        return result;
    }

    private static Matrix4x4 ToMatrix(TransformStep step) => step.Kind switch
    {
        TransformKind.Translate => Matrix4x4.CreateTranslation(step.Value),
        TransformKind.Scale => Matrix4x4.CreateScale(step.Value),
        TransformKind.Rotate => CreateRotation(step.Value),
        _ => Matrix4x4.Identity,
    };

    /// <summary>Euler angles in degrees, applied X then Y then Z.</summary>
    private static Matrix4x4 CreateRotation(Vector3 degrees)
    {
        const float ToRadians = MathF.PI / 180f;

        return Matrix4x4.CreateRotationX(degrees.X * ToRadians)
             * Matrix4x4.CreateRotationY(degrees.Y * ToRadians)
             * Matrix4x4.CreateRotationZ(degrees.Z * ToRadians);
    }
}
