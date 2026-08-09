using ChromaTest.Core.Model.Materials;

namespace ChromaTest.Core.Model.Geometry;

/// <summary>
/// A solid in the scene: a primitive or a CSG operation.
/// </summary>
/// <remarks>
/// Deliberately pure data — there is no <c>Intersect</c> method here. Ray/solid
/// intersection exists once, in GLSL, so there is no second implementation to drift out of
/// step with it. Anything that needs to walk the tree does so through
/// <see cref="ISolidVisitor"/>.
/// </remarks>
public abstract class Solid
{
    /// <summary>
    /// Declared on this solid, or null to inherit from the nearest ancestor that declares
    /// one. Resolution happens when the scene is compiled for the GPU, not here.
    /// </summary>
    public Material? Material { get; set; }

    public Transform Transform { get; set; } = Transform.Identity;

    /// <summary>Name used in diagnostics and in the hierarchy dump.</summary>
    public abstract string Kind { get; }

    public abstract void Accept(ISolidVisitor visitor);

    public abstract TResult Accept<TResult>(ISolidVisitor<TResult> visitor);
}
