using System.Numerics;

namespace Chroma.Core.Model;

/// <summary>
/// The three vectors a shader needs to build a primary ray:
/// <c>normalize(Forward + ndc.x * Right + ndc.y * Up)</c> for normalised device
/// coordinates in -1..1. <see cref="Right"/> and <see cref="Up"/> already carry the field
/// of view and aspect ratio.
/// </summary>
public readonly record struct RayBasis(Vector3 Forward, Vector3 Right, Vector3 Up);

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

    /// <summary>
    /// Builds the ray basis for a framebuffer of the given width/height ratio.
    /// </summary>
    /// <remarks>
    /// This lives in the model rather than in the renderer so it can be tested without a
    /// GL context. A sign error in a camera basis is invisible on inspection and produces
    /// a mirrored or upside-down image that is easy to mistake for a scene mistake.
    /// </remarks>
    public RayBasis CreateRayBasis(float aspect)
    {
        Vector3 forward = Vector3.Normalize(LookAt - Position);

        // Right-handed: with forward = -Z and up = +Y, this yields +X.
        Vector3 right = Vector3.Normalize(Vector3.Cross(forward, Up));

        // Re-derived rather than reusing Up, which is only a roll reference and need not
        // be perpendicular to the view direction.
        Vector3 up = Vector3.Cross(right, forward);

        float halfHeight = MathF.Tan(FovDegrees * MathF.PI / 360f);

        return new RayBasis(forward, right * halfHeight * aspect, up * halfHeight);
    }
}
