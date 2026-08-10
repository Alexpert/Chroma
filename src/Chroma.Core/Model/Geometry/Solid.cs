using Chroma.Core.Model.Materials;
using Chroma.Core.Sdl.Source;

namespace Chroma.Core.Model.Geometry;

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

    /// <summary>
    /// Where this solid was written, so a later stage can point a diagnostic at it.
    /// </summary>
    /// <remarks>
    /// Compilation to the GPU can fail on a solid — a singular transform, or a subtree
    /// needing more spans than the shader allows — and "some solid somewhere" is not a
    /// usable error message. A <see cref="SourceSpan"/> is a pair of integers with no tie
    /// to the grammar, so carrying one here leaves the model independent of the language
    /// in the sense that matters.
    /// </remarks>
    public SourceSpan Origin { get; set; }

    /// <summary>
    /// The loop that generated this solid, or null if it was written out by hand.
    /// </summary>
    /// <remarks>
    /// <see cref="Origin"/> is not enough once a loop exists. Every solid a loop emits shares
    /// one origin — the body is written once — so a span budget blown by five hundred
    /// generated spheres points at a line that is not the problem. The loop and its count
    /// are, and that is what this carries.
    /// </remarks>
    public LoopOrigin? Generator { get; set; }

    /// <summary>Name used in diagnostics and in the hierarchy dump.</summary>
    public abstract string Kind { get; }

    public abstract void Accept(ISolidVisitor visitor);

    public abstract TResult Accept<TResult>(ISolidVisitor<TResult> visitor);
}
