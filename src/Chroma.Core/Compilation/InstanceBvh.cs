using System.Numerics;

namespace Chroma.Core.Compilation;

/// <summary>
/// A bounding volume hierarchy over the instances of a scene, flattened for a shader that has
/// no stack.
/// </summary>
/// <remarks>
/// <para>
/// Instancing makes a scene's <i>program</i> independent of how many placements it holds. This
/// makes its <i>frame</i> independent too. Without it the shader would test every instance's box
/// against every ray, which for the ten thousand placements instancing now permits is a linear
/// scan where the whole point was to stop paying per placement.
/// </para>
/// <para>
/// Two properties are chosen for the shader's sake rather than the tree's.
/// </para>
/// <para>
/// <b>Escape indices, not a stack.</b> Nodes are laid out depth-first, so descending is
/// <c>++node</c> and skipping a subtree is a jump to the index stored in the node. Traversal
/// therefore carries one <c>int</c> and no array. That matters more here than anywhere: the
/// driver allocates storage per variable rather than per live range, and a traversal stack
/// declared in <c>traceScene</c> is a stack allocated at every inlined call site of it — the
/// failure recorded in documents/gpu-backends.md as <c>error C5041</c>.
/// </para>
/// <para>
/// <b>One instance per leaf.</b> A leaf holding four instances would need a count and a loop;
/// a leaf holding one needs neither, and the extra levels of tree cost two texels each. The
/// simpler shader is worth more than the shallower tree.
/// </para>
/// </remarks>
public static class InstanceBvh
{
    /// <param name="Escape">Where a ray that misses this node's box goes next.</param>
    /// <param name="Instance">
    /// The instance this leaf holds, as a position in <see cref="Result.Order"/>, or -1 for an
    /// interior node.
    /// </param>
    public readonly record struct Node(Aabb Bounds, int Escape, int Instance);

    /// <param name="Order">
    /// Which instance each slot holds, as an index into what <see cref="Build"/> was given. The
    /// build permutes instances so that a leaf's instance sits where the tree wants it, and the
    /// packer writes the instance table in this order.
    /// </param>
    public sealed record Result(IReadOnlyList<Node> Nodes, IReadOnlyList<int> Order);

    /// <summary>Bins per axis when looking for a split. Twelve is the usual place to stop.</summary>
    private const int Bins = 12;

    /// <summary>What one ray/box test costs, relative to descending into a subtree.</summary>
    /// <remarks>
    /// The number is not tuned and does not need to be: it only decides whether a split is worth
    /// making at all, and for the shapes a scene actually holds — pieces on a board, columns in a
    /// row — every split is.
    /// </remarks>
    private const float TraversalCost = 1f;

    public static Result Build(IReadOnlyList<Aabb> bounds)
    {
        if (bounds.Count == 0)
        {
            return new Result([], []);
        }

        int[] order = new int[bounds.Count];
        Vector3[] centroids = new Vector3[bounds.Count];

        for (int i = 0; i < bounds.Count; i++)
        {
            order[i] = i;
            centroids[i] = (bounds[i].Min + bounds[i].Max) * 0.5f;
        }

        var builder = new Builder(bounds, centroids, order);
        builder.Emit(0, bounds.Count);

        return new Result(builder.Nodes, order);
    }

    private sealed class Builder(IReadOnlyList<Aabb> bounds, Vector3[] centroids, int[] order)
    {
        public List<Node> Nodes { get; } = [];

        /// <summary>Writes the subtree over <c>order[lo..hi)</c> and returns its root's index.</summary>
        public int Emit(int lo, int hi)
        {
            int self = Nodes.Count;
            Aabb box = Enclosing(lo, hi);

            if (hi - lo == 1)
            {
                Nodes.Add(new Node(box, self + 1, lo));
                return self;
            }

            // Reserved now and rewritten once the subtree's size is known, which is the whole
            // reason the escape index can be an index rather than a count.
            Nodes.Add(default);

            int mid = Split(lo, hi, box);

            Emit(lo, mid);
            Emit(mid, hi);

            Nodes[self] = new Node(box, Nodes.Count, -1);
            return self;
        }

        /// <summary>
        /// Where to cut <c>order[lo..hi)</c>, by binned surface-area heuristic over all three
        /// axes, falling back to the middle when no cut is better than not cutting.
        /// </summary>
        private int Split(int lo, int hi, Aabb box)
        {
            Aabb centroidBox = Aabb.Empty;

            for (int i = lo; i < hi; i++)
            {
                Vector3 c = centroids[order[i]];
                centroidBox = new Aabb(Vector3.Min(centroidBox.Min, c), Vector3.Max(centroidBox.Max, c));
            }

            Vector3 extent = centroidBox.Max - centroidBox.Min;
            int bestAxis = -1;
            int bestBin = -1;
            float bestCost = TraversalCost * (hi - lo);   // the cost of not splitting at all

            for (int axis = 0; axis < 3; axis++)
            {
                float width = Component(extent, axis);

                if (width <= 0f)
                {
                    // Every centroid on this axis is in the same place, so no cut separates
                    // anything and the bin index below would divide by zero.
                    continue;
                }

                Aabb[] binBounds = new Aabb[Bins];
                int[] binCounts = new int[Bins];
                Array.Fill(binBounds, Aabb.Empty);

                float scale = Bins / width;
                float origin = Component(centroidBox.Min, axis);

                for (int i = lo; i < hi; i++)
                {
                    int index = order[i];
                    int bin = Math.Min(Bins - 1, (int)((Component(centroids[index], axis) - origin) * scale));

                    binBounds[bin] = Aabb.Union(binBounds[bin], bounds[index]);
                    binCounts[bin]++;
                }

                // Sweep once from each end so every candidate cut is one array read rather than
                // a rescan of the bins to its left and right.
                Aabb[] leftBox = new Aabb[Bins];
                int[] leftCount = new int[Bins];
                Aabb running = Aabb.Empty;
                int count = 0;

                for (int bin = 0; bin < Bins; bin++)
                {
                    running = Aabb.Union(running, binBounds[bin]);
                    count += binCounts[bin];
                    leftBox[bin] = running;
                    leftCount[bin] = count;
                }

                running = Aabb.Empty;
                count = 0;

                for (int bin = Bins - 1; bin > 0; bin--)
                {
                    running = Aabb.Union(running, binBounds[bin]);
                    count += binCounts[bin];

                    int leftSide = leftCount[bin - 1];

                    if (leftSide == 0 || count == 0)
                    {
                        continue;
                    }

                    float cost = (Area(leftBox[bin - 1]) * leftSide + Area(running) * count) / Area(box);

                    if (cost < bestCost)
                    {
                        bestCost = cost;
                        bestAxis = axis;
                        bestBin = bin;
                    }
                }
            }

            if (bestAxis < 0)
            {
                // Either nothing beat leaving the range whole, or every centroid coincides. The
                // range still has to be cut -- a leaf holds one instance -- so sort along the
                // widest axis and cut in the middle.
                int widest = extent.X >= extent.Y && extent.X >= extent.Z ? 0
                    : extent.Y >= extent.Z ? 1 : 2;

                Array.Sort(
                    order,
                    lo,
                    hi - lo,
                    Comparer<int>.Create((a, b) =>
                        Component(centroids[a], widest).CompareTo(Component(centroids[b], widest))));

                return (lo + hi) / 2;
            }

            float splitOrigin = Component(centroidBox.Min, bestAxis);
            float splitScale = Bins / Component(extent, bestAxis);

            int left = lo;
            int right = hi - 1;

            while (left <= right)
            {
                int bin = Math.Min(
                    Bins - 1,
                    (int)((Component(centroids[order[left]], bestAxis) - splitOrigin) * splitScale));

                if (bin < bestBin)
                {
                    left++;
                }
                else
                {
                    (order[left], order[right]) = (order[right], order[left]);
                    right--;
                }
            }

            // The sweep above only considered cuts with something on both sides, so this cannot
            // land on an end -- but a degenerate float could, and an empty side would recurse for
            // ever rather than fail.
            return Math.Clamp(left, lo + 1, hi - 1);
        }

        private Aabb Enclosing(int lo, int hi)
        {
            Aabb box = Aabb.Empty;

            for (int i = lo; i < hi; i++)
            {
                box = Aabb.Union(box, bounds[order[i]]);
            }

            return box;
        }
    }

    private static float Component(Vector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;

    /// <summary>Surface area, and zero for a box that encloses nothing.</summary>
    private static float Area(Aabb box)
    {
        if (box.IsEmpty)
        {
            return 0f;
        }

        Vector3 d = box.Max - box.Min;
        return 2f * ((d.X * d.Y) + (d.Y * d.Z) + (d.Z * d.X));
    }
}
