using System.Numerics;

namespace ChromaTest.Core.Model.Materials;

/// <summary>
/// Surface appearance, in the metallic-roughness parameterisation.
/// </summary>
/// <remarks>
/// <para>
/// Four fields, chosen because they are the ones an author can reason about. See
/// <c>documents/lighting.md</c> for the BRDF they drive and for why a metal has no diffuse
/// lobe at all.
/// </para>
/// <para>
/// This replaced a Blinn-Phong <c>specular</c>/<c>shininess</c>/<c>reflectivity</c> triple.
/// Blinn-Phong is not energy-conserving, which is harmless for direct lighting and
/// incoherent once light is summed over bounces.
/// </para>
/// </remarks>
public sealed record Material
{
    public static readonly Material Default = new();

    /// <summary>
    /// Base colour, linear, each component in 0..1. Diffuse albedo for a dielectric; the
    /// reflectance tint for a metal.
    /// </summary>
    public Vector3 Color { get; init; } = new(0.8f, 0.8f, 0.8f);

    /// <summary>0 is mirror-smooth, 1 is fully matte.</summary>
    public float Roughness { get; init; } = 0.5f;

    /// <summary>0 is a dielectric, 1 is a metal. A mirror is metallic 1, roughness 0.</summary>
    public float Metallic { get; init; }

    /// <summary>
    /// Radiance emitted by the surface, unbounded — this is a light, not a colour, so
    /// values above 1 are ordinary.
    /// </summary>
    public Vector3 Emission { get; init; }

    /// <summary>
    /// The <c>let</c> binding this material came from, when it came from one. Used only to
    /// print <c>material=red</c> instead of a list of components in the hierarchy dump.
    /// </summary>
    public string? Name { get; init; }
}
