using System.Numerics;
using Chroma.Core.Model.Geometry;
using Chroma.Core.Model.Materials;

namespace Chroma.Core.Compilation;

/// <summary>
/// One appearance of a shape in the world: where it stands and what it is made of.
/// </summary>
/// <param name="Root">
/// This appearance's own subtree, as <see cref="ShapeCanonicalizer"/> peeled it.
/// </param>
/// <param name="Placement">
/// Shape space to world, and what goes in the instance record. Recovered by
/// <see cref="ShapeCanonicalizer"/>, which is what lets two of these share one emitted body.
/// </param>
/// <param name="Spine">
/// Only what was peeled off the <i>spine</i> of the root — the wrappers' transforms, without the
/// normalisation that lets a solid carrying its own position be recognised.
/// </param>
/// <param name="Materials">
/// The materials this appearance wears, in slot order — one entry per slot the shape declares,
/// so <c>Materials[slot]</c> is what the leaf carrying that slot is made of.
/// </param>
/// <remarks>
/// <para>
/// An appearance keeps its own tree and its own spine even though its group has a tree of its
/// own, and the reason is the whole of a bug that made a cornell box lose its ceiling. The
/// group's <see cref="ShapeGroup.Root"/> is whichever root reached it first, and the position
/// that distinguishes the others has been normalised out of it into
/// <see cref="ShapeGroup.ShapeFrame"/>. Emitting the group's tree once per appearance therefore
/// draws every one of them on top of the first.
/// </para>
/// <para>
/// So a shared body is emitted from the group's tree at the shape's origin and reaches the world
/// through <paramref name="Placement"/>, while a folded one is emitted from
/// <paramref name="Root"/> at <paramref name="Spine"/> — which is byte-for-byte what every root
/// was before instancing existed. That is what makes "below the threshold, nothing changes" a
/// property of the code rather than an argument about floating point.
/// </para>
/// </remarks>
public sealed record ShapePlacement(
    Solid Root,
    Matrix4x4 Placement,
    Matrix4x4 Spine,
    IReadOnlyList<Material> Materials);

/// <summary>
/// One distinct shape, and everywhere it stands.
/// </summary>
/// <remarks>
/// A group with a single placement is a <b>singleton</b> and is emitted exactly as every root
/// was before instancing existed: its placement folded into its leaves as <c>const mat4</c>
/// literals and its own guarded block in <c>traceScene</c>. A group with two or more is
/// <b>instanced</b>: one body, reached from a loop the driver cannot unroll, with the placement
/// arriving from a buffer. See documents/gpu-backends.md.
/// </remarks>
public sealed class ShapeGroup
{
    /// <summary>
    /// The subtree that is the shape. Its own <see cref="Solid.Transform"/> is part of every
    /// placement and must <b>not</b> be applied again when it is walked.
    /// </summary>
    public required Solid Root { get; init; }

    /// <summary>What two roots have to agree on to be the same shape. See <see cref="ShapeCanonicalizer"/>.</summary>
    public required string Key { get; init; }

    /// <summary>
    /// The frame a shared body is emitted in: the transform that takes <see cref="Root"/>'s own
    /// space to the shape's.
    /// </summary>
    /// <remarks>
    /// A translation, and it is what lets two solids written with their positions <i>inside</i>
    /// them — <c>sphere { center: p }</c>, <c>box { min: … max: … }</c> — be recognised as one
    /// shape. Only <c>translate:</c> on the solid itself comes off in <see cref="ShapePlacement"/>;
    /// this takes off the rest. Being a pure translation is what makes it exact: composing one
    /// with its own negation cancels to the last bit, where inverting a general matrix does not.
    /// </remarks>
    public required Matrix4x4 ShapeFrame { get; init; }

    /// <summary>
    /// Which material slot each leaf wears, in the order the emitter will reach them.
    /// </summary>
    /// <remarks>
    /// Computed once here rather than twice. The emitter used to resolve material inheritance
    /// itself while walking; it now reads this list, so the rule for what a leaf is made of
    /// exists in one place and cannot drift between the thing that decides two shapes are equal
    /// and the thing that emits them.
    /// </remarks>
    public required IReadOnlyList<int> LeafSlots { get; init; }

    public List<ShapePlacement> Placements { get; } = [];

    /// <summary>How many distinct materials one appearance of this shape wears.</summary>
    public int MaterialSlots => Placements[0].Materials.Count;

    /// <summary>Whether this shape reaches the ray through the instance buffer.</summary>
    /// <remarks>Set by <see cref="ShapePartition.ShareFrom"/>, not by the shape itself.</remarks>
    public bool Instanced { get; internal set; }
}

/// <summary>
/// Every root in the scene, sorted into the shapes they are appearances of.
/// </summary>
public sealed class ShapePartition
{
    /// <summary>
    /// How many repeated placements a scene needs before instancing is worth its cost.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Instancing is not free, and the cost is not where it looks. The buffer read and the second
    /// matrix are small; what costs is that a BVH walk is a loop of <b>dependent</b> memory reads,
    /// where the folded form is a run of independent <c>if</c>s the compiler can interleave and
    /// the branch predictor can get right. Under a few dozen placements the linear scan of folded
    /// guards simply wins — measured on an RTX 4070 SUPER at 512 samples and 1280x720, sharing
    /// everything shareable cost <c>glass</c> 35% and <c>cornell</c> 18%, while paying
    /// <c>chess</c> 5.0x, <c>lattice</c> 3.5x and <c>chess-half</c> 3.0x.
    /// </para>
    /// <para>
    /// The question is asked of the <b>scene</b> rather than of each shape, because what decides
    /// it is the depth of one tree over every placement rather than how often any one shape
    /// appears. A chess set whose pawn stands in sixteen places and whose queen stands in two
    /// wants both in the same tree; deciding shape by shape would leave the queen folded, which is
    /// the case that made <c>chess-full</c> refuse to compile in the first place.
    /// </para>
    /// <para>
    /// Thirty-two is where the two groups separate with room on either side. It is a guess about
    /// speed and nothing more, so it is overridden by <see cref="ShareEverything"/> when the
    /// driver refuses the program: a scene that will not compile has no speed to protect.
    /// </para>
    /// </remarks>
    public const int DefaultShareFrom = 32;

    /// <summary>Every shareable shape is shared, whatever it costs. The fallback.</summary>
    public const int ShareEverything = 2;

    public required IReadOnlyList<ShapeGroup> Shapes { get; init; }

    /// <summary>
    /// Decides whether shapes are reached through the buffer or keep their placements folded into
    /// their leaves.
    /// </summary>
    /// <remarks>
    /// A shape the canonicaliser refused to share — one holding a <c>plane</c>, or placed by a
    /// mirroring transform — has exactly one appearance in its group, so it falls out of this as a
    /// singleton without a special case.
    /// </remarks>
    public void ShareFrom(int placements)
    {
        bool worth = Shapes.Where(shape => shape.Placements.Count > 1)
            .Sum(shape => shape.Placements.Count) >= placements;

        foreach (ShapeGroup shape in Shapes)
        {
            shape.Instanced = worth && shape.Placements.Count > 1;
        }
    }

    public int InstanceCount => Shapes.Where(shape => shape.Instanced).Sum(shape => shape.Placements.Count);

    public int SingletonCount => Shapes.Count(shape => !shape.Instanced);
}
