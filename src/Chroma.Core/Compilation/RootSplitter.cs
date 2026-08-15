using Chroma.Core.Codegen;
using Chroma.Core.Model.Geometry;
using Chroma.Core.Model.Geometry.Operations;
using Chroma.Core.Model.Materials;

namespace Chroma.Core.Compilation;

/// <summary>
/// Cuts a shape too large to emit into the operands of its top-level <c>union</c>.
/// </summary>
/// <remarks>
/// <para>
/// The step before <see cref="SceneChunker"/>, and it exists because chunking could not reach the
/// one scene that needed it. A chunk cuts between whole shapes, so <c>scenes/cube.chroma</c> --
/// eight thousand boxes in nested <c>union</c>s, and therefore <b>one</b> shape with eight thousand
/// leaves -- had nothing to cut on and was refused. This cuts inside the shape instead.
/// </para>
/// <para>
/// There is no new machinery for it, because a scene's roots are <b>already</b> an implicit union
/// that is resolved one at a time: <see cref="GeometryEmitter.EmitShape"/> gives each root its own
/// function, its own bounds test and its own span list, and <c>traceScene</c> folds each into the
/// running nearest hit. Cutting a <c>union</c> root into one root per operand says exactly that,
/// in the terms the rest of the compiler already speaks.
/// </para>
/// <para>
/// And the cut is not really how the scene gets smaller. The pieces go to
/// <see cref="ShapeCanonicalizer"/>, which then sees what it never could before: the twenty
/// sub-cubes of <c>cube(3)</c> are the <i>same shape</i> standing in twenty places. Cutting is what
/// lets instancing collapse the scene, rather than a way of spreading it over more programs.
/// </para>
/// <para>
/// What it costs is coalescing. Two operands of one <c>union</c> merge into a single interval where
/// they overlap; as separate roots they do not, which is the limitation
/// documents/csg-raytracing.md already records for roots and documents/implementation.md already
/// lists twice under "wrap them in an explicit <c>union</c>". For opaque solids nothing of it is
/// visible from outside; for transmissive ones it changes the picture. Hence <see cref="Cuttable"/>
/// is a question about the operands rather than a blanket rule. See documents/cutting-unions.md.
/// </para>
/// </remarks>
public static class RootSplitter
{
    /// <summary>
    /// Partitions a scene, cutting shapes apart until each is one a program could hold.
    /// </summary>
    /// <param name="roots">The scene as written. Read and never written.</param>
    /// <param name="shareFrom">See <see cref="ShapePartition.DefaultShareFrom"/>.</param>
    /// <param name="budget">See <see cref="ShapeCost.Budget"/>.</param>
    /// <remarks>
    /// <para>
    /// Returns the partition rather than the roots, because the two must not be able to drift and
    /// because partitioning a large scene twice is a second's work this would rather not spend.
    /// </para>
    /// <para>
    /// A scene that fits is returned untouched, by the same shortcut and for the same reason
    /// <see cref="SceneChunker.Split"/> takes one: the overwhelmingly common case deserves to be
    /// obviously unchanged, and every scene that compiles today is then byte-for-byte what it was
    /// as a matter of construction rather than of measurement.
    /// </para>
    /// </remarks>
    public static ShapePartition Cut(IReadOnlyList<Solid> roots, int shareFrom, int budget)
    {
        ShapePartition partition = Partitioned(roots, shareFrom, budget);

        if (partition.Estimate() <= budget)
        {
            return partition;
        }

        while (true)
        {
            // Only a shape that cannot fit a program ON ITS OWN, or that carries more span list
            // than a thread should. A scene merely over budget in aggregate is what chunking is
            // for and is left to it: chunking costs a second pass of the path tracer, cutting
            // costs the coalescing between two operands, and the second is the dearer of the two
            // to spend on a scene that has an answer already. This is why palisade, two hundred
            // posts of which none is near the budget, falls straight through here.
            HashSet<ShapeGroup> wanted =
            [
                .. partition.Shapes.Where(shape =>
                    shape.Weight > budget || shape.Spans > ShapeCost.MaxSpans),
            ];

            if (wanted.Count == 0)
            {
                return partition;
            }

            List<Solid> next = [];

            for (int i = 0; i < roots.Count; i++)
            {
                if (wanted.Contains(partition.GroupOfRoot[i]))
                {
                    next.AddRange(Apart(roots[i]));
                    continue;
                }

                next.Add(roots[i]);
            }

            // Nothing had a seam to cut on. A forty-segment lathe is one leaf however wide its
            // list is, and a scene of them is refused as it always was, by the driver, with the
            // diagnostic that names the shapes.
            if (next.Count == roots.Count)
            {
                return partition;
            }

            roots = next;
            partition = Partitioned(roots, shareFrom, budget);
        }
    }

    private static ShapePartition Partitioned(IReadOnlyList<Solid> roots, int shareFrom, int budget)
    {
        ShapePartition partition = ShapeCanonicalizer.Partition(roots);
        partition.Choose(shareFrom, budget);
        return partition;
    }

    /// <summary>
    /// One root per operand of the root's <c>union</c>, or the root alone if it cannot be cut.
    /// </summary>
    private static IEnumerable<Solid> Apart(Solid root)
    {
        IReadOnlyList<Solid> spine = ShapeCanonicalizer.Spine(root);
        Solid shape = spine[^1];

        Material? inherited = null;

        foreach (Solid node in spine)
        {
            inherited = node.Material ?? inherited;
        }

        if (shape is not Union union || !Cuttable(union, inherited))
        {
            return [root];
        }

        return union.Operands.Select(operand => Wrap(spine, operand));
    }

    /// <summary>Whether the operands of a <c>union</c> may be resolved separately.</summary>
    /// <remarks>
    /// <para>
    /// The one thing separate roots lose is that two overlapping intervals stop merging into one.
    /// For an opaque pair that is invisible from outside — the nearer entry is the nearer entry
    /// either way — and the case it does get wrong, a ray starting inside both at once, is the one
    /// documents/csg-raytracing.md already records for roots that were written separately. For a
    /// <b>transmissive</b> pair it is a lens-shaped seam where the two meet, which is a picture
    /// changing and not a bound loosening.
    /// </para>
    /// <para>
    /// So the test is the pair and not the union: transmissive operands that stand apart are cut
    /// freely, and only two that actually overlap stop the cut. Bounds are asked for at all only
    /// when two operands could be transmissive, which on a scene of plain solids costs one walk
    /// over the materials and no probes.
    /// </para>
    /// </remarks>
    private static bool Cuttable(Union union, Material? inherited)
    {
        if (union.Operands.Count < 2)
        {
            return false;
        }

        List<Solid> glass =
        [
            .. union.Operands.Where(operand => Transmissive(operand, inherited)),
        ];

        if (glass.Count < 2)
        {
            return true;
        }

        List<Aabb> boxes = [.. glass.Select(operand => Bounds(operand, inherited))];

        for (int a = 0; a < boxes.Count; a++)
        {
            for (int b = a + 1; b < boxes.Count; b++)
            {
                if (!Aabb.Intersect(boxes[a], boxes[b]).IsEmpty)
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>Whether any leaf of a subtree lets light through it.</summary>
    /// <remarks>
    /// Scattering counts as well as transmission: a participating medium is entered and left, so
    /// where its boundary sits is as visible as a glass one's.
    /// </remarks>
    private static bool Transmissive(Solid solid, Material? inherited)
    {
        Material? effective = solid.Material ?? inherited;

        if (solid is CsgOperation operation)
        {
            return operation.Operands.Any(operand => Transmissive(operand, effective));
        }

        Material material = effective ?? Material.Default;
        return material.Transmission > 0f || material.Scattering > 0f;
    }

    /// <summary>The box an operand occupies, in the space of the <c>union</c> holding it.</summary>
    /// <remarks>
    /// Its own transform is passed as the ancestor because <see cref="GeometryEmitter.Probe"/>
    /// emits its argument the way a shape root is emitted, without applying the node's own
    /// transform — that being the appearance's business everywhere else. Here it is not: two
    /// siblings are only comparable once each stands where its parent puts it.
    /// </remarks>
    private static Aabb Bounds(Solid operand, Material? inherited)
    {
        List<Material> materials = [];
        List<int> slots = [];
        ShapeCanonicalizer.Resolve(operand, inherited, materials, slots);

        return GeometryEmitter.Probe(operand, operand.Transform.Matrix, slots).Bounds;
    }

    /// <summary>Rebuilds a root's spine around one operand of the shape it ended in.</summary>
    /// <remarks>
    /// <para>
    /// The last element of the spine is the <c>union</c> being cut, so its own <c>scale:</c> and
    /// its own material are carried onto every piece and inherited exactly as they were. Nothing
    /// is composed and nothing is inverted: a <see cref="Transform"/> is immutable and is passed
    /// by reference, so there is no arithmetic here to round.
    /// </para>
    /// <para>
    /// Every wrapper is rebuilt as a <c>union</c> of one whatever it was, and nothing is lost by
    /// it: an <c>intersection</c> or a <c>difference</c> of a single operand is that operand.
    /// <see cref="ShapeCanonicalizer.Spine"/> walks them all off again before anything is emitted,
    /// so what reaches the emitter is the operand's own subtree under the same total transform it
    /// had inside the union.
    /// </para>
    /// </remarks>
    private static Solid Wrap(IReadOnlyList<Solid> spine, Solid operand)
    {
        Solid built = operand;

        for (int i = spine.Count - 1; i >= 0; i--)
        {
            built = new Union
            {
                Operands = [built],
                Transform = spine[i].Transform,
                Material = spine[i].Material,

                // The wrapper is peeled off before anything is emitted, so what a diagnostic
                // eventually points at is the operand's own origin and the loop that made it --
                // the box and its `for`, rather than the union at the top of the file. These are
                // carried anyway so that a wrapper is never the thing with no origin at all.
                Origin = spine[i].Origin,
                Generator = spine[i].Generator,
            };
        }

        return built;
    }
}
