using System.Numerics;

namespace Chroma.Core.Model.Lighting;

public abstract class Light
{
    public Vector3 Color { get; init; } = Vector3.One;

    public float Intensity { get; init; } = 1f;

    public abstract string Kind { get; }
}

public sealed class PointLight : Light
{
    public Vector3 Position { get; init; }

    /// <summary>
    /// Radius of the emitting sphere. Zero is an idealised point, which is the delta case
    /// and gives hard shadows; above zero the renderer samples the sphere and the shadow
    /// gains a penumbra.
    /// </summary>
    /// <remarks>
    /// It is a pure softness control: the radiance is normalised so that changing the
    /// radius does not change how brightly the light illuminates the scene. See
    /// <c>documents/lighting.md</c> for the normalisation and why it agrees with the point
    /// case in the limit.
    /// </remarks>
    public float Radius { get; init; }

    public override string Kind => "PointLight";
}

/// <summary>
/// A light infinitely far away. <see cref="Direction"/> is the direction the light
/// travels towards, normalised when the scene is bound.
/// </summary>
public sealed class DirectionalLight : Light
{
    public Vector3 Direction { get; init; } = new(0f, -1f, 0f);

    public override string Kind => "DirectionalLight";
}
