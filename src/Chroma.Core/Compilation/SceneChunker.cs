namespace Chroma.Core.Compilation;

/// <summary>
/// Splits a scene's shapes into groups small enough to compile, one program each.
/// </summary>
/// <remarks>
/// <para>
/// The last resort, and deliberately so. <see cref="ShapePartition.Choose"/> runs first and shares
/// what it can, because sharing makes a program smaller for the price of a buffer read; chunking
/// makes it fit for the price of running the whole path tracer once per chunk. A scene that can be
/// made to fit is made to fit, and only a scene that cannot is split.
/// </para>
/// <para>
/// A chunk is a set of whole <b>shapes</b>. It never cuts inside one, which is why
/// <c>scenes/cube.chroma</c> — eight thousand boxes in a single <c>union</c>, one shape with eight
/// thousand leaves — is still refused rather than split, and refused with a diagnostic that says
/// exactly that. Cutting inside a top-level <c>union</c> would work for nearest-hit and is written
/// up as a question in documents/instancing.md; it stops two overlapping transmissive children
/// coalescing into one interval, which is a real difference and not one to make silently.
/// </para>
/// </remarks>
public static class SceneChunker
{
    /// <summary>
    /// Assigns every shape to a chunk, in the order a chunk should emit them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// First-fit-decreasing. The shapes do not interact — a chunk's cost is the sum of what is in
    /// it and nothing else — so packing the dearest first is the standard bound and there is
    /// nothing subtler to be had for the trouble.
    /// </para>
    /// <para>
    /// Bins are packed by <b>cost</b> and not by position. Packing spatially too would let each
    /// chunk's bounding volumes reject more rays, since a chunk of shapes standing near each other
    /// has a tighter tree than a chunk assembled by weight. Whether that is worth anything is a
    /// measurement nobody has taken, so it is written down rather than guessed at.
    /// </para>
    /// <para>
    /// Within a chunk the shapes come back in the order the partition held them, not in the order
    /// the packing considered them. That is what makes a one-chunk scene emit byte-for-byte the
    /// text it emitted before chunking existed: emission order decides leaf numbering, and leaf
    /// numbering is a literal in the generated GLSL.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<IReadOnlyList<ShapeGroup>> Split(ShapePartition partition, int budget)
    {
        // Nothing to decide, and taking the shortcut rather than trusting the loop to reach the
        // same place: the overwhelmingly common case deserves to be obviously unchanged.
        if (partition.Estimate() <= budget)
        {
            return [partition.Shapes];
        }

        List<int> loads = [];
        Dictionary<ShapeGroup, int> assignment = [];

        // Where each shape stands in the partition, for the tie-break below and for putting the
        // chunks back in order at the end.
        Dictionary<ShapeGroup, int> written = [];
        for (int i = 0; i < partition.Shapes.Count; i++)
        {
            written[partition.Shapes[i]] = i;
        }

        IEnumerable<ShapeGroup> dearestFirst = partition.Shapes
            .OrderByDescending(shape => shape.Weight)

            // A stable tie-break, so two runs of the same scene chunk the same way. Ordering by
            // weight alone leaves shapes of equal weight in an order nothing promises, and a scene
            // that chunks differently between runs is a scene whose images cannot be compared.
            .ThenBy(shape => written[shape]);

        foreach (ShapeGroup shape in dearestFirst)
        {
            int chosen = -1;

            for (int bin = 0; bin < loads.Count; bin++)
            {
                if (loads[bin] + shape.Weight <= budget)
                {
                    chosen = bin;
                    break;
                }
            }

            if (chosen < 0)
            {
                // Either nothing has room, or this one shape is larger than the budget and never
                // will have. The second case gets a chunk to itself and that chunk is over budget,
                // which is honest: the driver will refuse it, and the refusal names the shape.
                chosen = loads.Count;
                loads.Add(0);
            }

            loads[chosen] += shape.Weight;
            assignment[shape] = chosen;
        }

        List<List<ShapeGroup>> chunks = [.. loads.Select(_ => new List<ShapeGroup>())];

        foreach (ShapeGroup shape in partition.Shapes)
        {
            chunks[assignment[shape]].Add(shape);
        }

        return chunks;
    }
}
