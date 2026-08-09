using System.Numerics;

namespace ChromaTest.Core.Model;

/// <summary>
/// The eye. Right-handed space, +X right, +Y up, +Z towards the viewer.
/// </summary>
public sealed class Camera
{
    public Vector3 Position { get; init; } = new(0f, 0f, 5f);

    public Vector3 LookAt { get; init; } = Vector3.Zero;

    public Vector3 Up { get; init; } = Vector3.UnitY;

    /// <summary>Vertical field of view, in degrees.</summary>
    public float FovDegrees { get; init; } = 45f;
}
