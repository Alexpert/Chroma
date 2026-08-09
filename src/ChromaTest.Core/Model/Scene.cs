using ChromaTest.Core.Model.Geometry;
using ChromaTest.Core.Model.Lighting;

namespace ChromaTest.Core.Model;

/// <summary>
/// A loaded scene: everything a renderer needs, and nothing about how it will be rendered.
/// </summary>
public sealed class Scene
{
    public required Camera Camera { get; init; }

    public required IReadOnlyList<Light> Lights { get; init; }

    /// <summary>
    /// Top-level solids. They are implicitly unioned; keeping them as a list rather than
    /// wrapping them in a synthetic <see cref="Operations.Union"/> means the dump shows
    /// the file as written.
    /// </summary>
    public required IReadOnlyList<Solid> Roots { get; init; }
}
