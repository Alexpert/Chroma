using System.Numerics;

namespace ChromaTest.Core.Model.Lighting;

public abstract class Light
{
    public Vector3 Color { get; init; } = Vector3.One;

    public float Intensity { get; init; } = 1f;

    public abstract string Kind { get; }
}

public sealed class PointLight : Light
{
    public Vector3 Position { get; init; }

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
