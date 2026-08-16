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

    /// <summary>What one appearance of this shape weighs. See <see cref="ShapeCost"/>.</summary>
    /// <remarks>
    /// Reported by the probe that computed <see cref="Key"/>, from the same walk, so the number
    /// the partition decides on is the number the emitter will produce. A shared shape pays it
    /// once; a folded one pays it once per placement, and that difference is the whole of
    /// <see cref="ShapePartition.Estimate"/>.
    /// </remarks>
    public required int Cost { get; init; }

    /// <summary>How wide one appearance's span list is at worst.</summary>
    /// <remarks>
    /// How much state a thread carries while this shape is resolved, and the second thing that
    /// can make a shape too big to be worth emitting whole. Unlike <see cref="Cost"/> it does not
    /// depend on how the shape is reached: a shared body and a folded one resolve the same tree
    /// into the same list. See <see cref="ShapeCost.MaxSpans"/> and <see cref="RootSplitter"/>.
    /// </remarks>
    public required int Spans { get; init; }

    public List<ShapePlacement> Placements { get; } = [];

    /// <summary>How many distinct materials one appearance of this shape wears.</summary>
    public int MaterialSlots => Placements[0].Materials.Count;

    /// <summary>Whether this shape reaches the ray through the instance buffer.</summary>
    /// <remarks>Set by <see cref="ShapePartition.Choose"/>, not by the shape itself.</remarks>
    public bool Instanced { get; internal set; }

    /// <summary>What this shape adds to the program it is emitted into.</summary>
    /// <remarks>
    /// A shared shape's body is written once however many appearances it has, which is the whole
    /// mechanism; a folded one is written out at each of them, which is what it was before
    /// instancing existed. Both <see cref="ShapePartition.Estimate"/> and
    /// <see cref="SceneChunker"/> ask this question, and asking it in one place is what stops the
    /// chunker packing bins against a number the estimate does not agree with.
    /// </remarks>
    public int Weight => Instanced ? Cost : Cost * Placements.Count;
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

    /// <summary>Which group each root turned out to belong to, in the order they were given.</summary>
    /// <remarks>
    /// A by-product of the walk that built the partition, kept because
    /// <see cref="RootSplitter"/> decides on a <i>shape</i> and has to act on the <i>roots</i>
    /// that produced it. Nothing else reads it, and it is one list rather than a field on
    /// <see cref="ShapePlacement"/> so that a placement stays a description of an appearance
    /// rather than a back-pointer into whatever was being compiled at the time.
    /// </remarks>
    public required IReadOnlyList<ShapeGroup> GroupOfRoot { get; init; }

    /// <summary>
    /// Decides whether shapes are reached through the buffer or keep their placements folded into
    /// their leaves.
    /// </summary>
    /// <param name="shareFrom">
    /// How many repeated placements the scene needs before sharing is worth its cost. A question
    /// about <b>speed</b>. See <see cref="DefaultShareFrom"/>.
    /// </param>
    /// <param name="budget">
    /// What the program may weigh. A question about <b>feasibility</b>, and a different one: a
    /// scene that will not compile has no speed to protect. See <see cref="ShapeCost.Budget"/>.
    /// </param>
    /// <remarks>
    /// <para>
    /// The two questions compose rather than compete, because sharing only ever makes the program
    /// smaller: the budget can add to what speed already wanted and never take any of it away. So
    /// a scene that fits today is decided exactly as it was before the budget existed, and the new
    /// rule bites only where the old one had nothing to offer.
    /// </para>
    /// <para>
    /// Where the old one had nothing to offer was the scene below the threshold that overflows
    /// anyway. Its only answer was to share <i>everything</i> and lose the folded form's speed on
    /// every shape in the scene to fix a problem caused by two of them. Now it sheds the expensive
    /// ones, in order, and stops as soon as it fits.
    /// </para>
    /// <para>
    /// What this deliberately does not do is <b>unshare</b> when there is room to spare. It might
    /// be faster: a chessboard's squares are cheap and could be folded back while the pieces stay
    /// shared. It might not, since the Phase 1 measurement says the gain is the tree rather than
    /// the sharing, and a folded shape is a linear guard outside the tree. Nobody has measured it,
    /// and guessing risks a 5.8x. See documents/instancing.md.
    /// </para>
    /// <para>
    /// A shape the canonicaliser refused to share — one holding a <c>plane</c>, or placed by a
    /// mirroring transform — has exactly one appearance in its group, so it falls out of this as a
    /// singleton without a special case.
    /// </para>
    /// </remarks>
    public void Choose(int shareFrom, int budget)
    {
        // Speed first, and unchanged: a scene with enough repetition to pay for a tree gets one.
        bool worth = Shapes.Where(shape => shape.Placements.Count > 1)
            .Sum(shape => shape.Placements.Count) >= shareFrom;

        foreach (ShapeGroup shape in Shapes)
        {
            shape.Instanced = worth && shape.Placements.Count > 1;
        }

        // Then feasibility. Greedy by what each choice saves, which is exact rather than a
        // heuristic: the shapes do not interact, so taking the largest saving each time is the
        // fewest shapes that will get under any given budget.
        //
        // Having two placements is the whole test for being shareable. A root the canonicaliser
        // refused is never interned under its key, so it sits alone in a group of its own and no
        // second placement can ever join it.
        List<ShapeGroup> candidates =
        [
            .. Shapes
                .Where(shape => !shape.Instanced && shape.Placements.Count > 1)
                .OrderByDescending(shape => shape.Cost * (shape.Placements.Count - 1)),
        ];

        // Carried rather than re-summed, so a scene of ten thousand distinct shapes costs one
        // pass here rather than one pass per shape shed.
        int estimate = Estimate();

        foreach (ShapeGroup shape in candidates)
        {
            if (estimate <= budget)
            {
                return;
            }

            shape.Instanced = true;
            estimate -= shape.Cost * (shape.Placements.Count - 1);
        }
    }

    /// <summary>What the scene weighs as currently partitioned. See <see cref="ShapeCost"/>.</summary>
    public int Estimate() => Shapes.Sum(shape => shape.Weight);

    public int InstanceCount => Shapes.Where(shape => shape.Instanced).Sum(shape => shape.Placements.Count);

    public int SingletonCount => Shapes.Count(shape => !shape.Instanced);
}
