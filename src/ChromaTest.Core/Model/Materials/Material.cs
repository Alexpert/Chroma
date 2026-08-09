using System.Numerics;

namespace ChromaTest.Core.Model.Materials;

/// <summary>
/// Surface appearance. Only the fields the shader currently reads are here;
/// <see cref="Reflectivity"/> is carried but ignored until reflections exist.
/// </summary>
public sealed record Material
{
    public static readonly Material Default = new();

    /// <summary>Diffuse albedo, linear, each component in 0..1.</summary>
    public Vector3 Color { get; init; } = new(0.8f, 0.8f, 0.8f);

    public float Specular { get; init; }

    public float Shininess { get; init; } = 32f;

    public float Reflectivity { get; init; }

    /// <summary>
    /// The <c>let</c> binding this material came from, when it came from one. Used only to
    /// print <c>material=red</c> instead of a list of components in the hierarchy dump.
    /// </summary>
    public string? Name { get; init; }
}
