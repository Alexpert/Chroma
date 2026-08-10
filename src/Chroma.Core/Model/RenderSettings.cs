namespace Chroma.Core.Model;

/// <summary>
/// How the scene should be rendered, as opposed to what it contains.
/// </summary>
/// <remarks>
/// These live in the scene file rather than in the build because they are properties of a
/// scene: exposure that suits a sunlit exterior blows out a candlelit interior, and a scene
/// with mirrors needs more bounces than one without. POV-Ray keeps the same distinction
/// under <c>global_settings</c>.
/// </remarks>
public sealed record RenderSettings
{
    public static readonly RenderSettings Default = new();

    /// <summary>Smallest and largest accepted <see cref="MaxBounces"/>, inclusive.</summary>
    /// <remarks>
    /// One bounce is direct lighting only, which is a legitimate thing to ask for when
    /// comparing against it. The upper bound exists because the loop runs per pixel per
    /// frame: an absurd depth is a typing mistake, and a frozen driver costs far more to
    /// diagnose than a diagnostic.
    /// </remarks>
    public const int MinBounces = 1;
    public const int MaxAllowedBounces = 16;

    /// <summary>Path length: 1 is direct lighting only, higher lets light bounce.</summary>
    public int MaxBounces { get; init; } = 4;

    /// <summary>Multiplier applied to accumulated radiance before tone mapping.</summary>
    public float Exposure { get; init; } = 1f;
}
