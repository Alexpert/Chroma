using System.Numerics;
using Chroma.Core.Codegen;
using Chroma.Core.Model.Geometry;
using Chroma.Core.Model.Geometry.Operations;
using Chroma.Core.Model.Materials;

// System.Numerics has a Plane of its own — a mathematical plane, not a solid. The alias says
// which one is meant once, as GeometryEmitter does.
using Plane = Chroma.Core.Model.Geometry.Primitives.Plane;

namespace Chroma.Core.Compilation;

/// <summary>
/// Finds out which roots are the same shape standing somewhere else.
/// </summary>
/// <remarks>
/// <para>
/// The language gives no help here, deliberately: a <c>let</c> stores an unbound block and a
/// <c>function</c> returns a fresh tree at every call, so <c>pawn(0, 1)</c> and <c>pawn(1, 1)</c>
/// share no object identity and nothing in the model says they are the same piece. Shape
/// identity is therefore <b>recovered</b> here rather than declared by the author, which is what
/// keeps the change out of the language.
/// </para>
/// <para>
/// Two roots are the same shape when they <b>emit the same GLSL</b>. That is the definition, not
/// an approximation of one, and it is the reason there is no list of what to compare about a
/// sphere in this file: what a solid is is what it compiles to, so <see cref="GeometryEmitter"/>
/// is asked. It is <c>LeafEmitter.KeyOf</c>'s discipline — compare the literals a solid becomes,
/// not the floats it was written with — widened from a leaf to a whole tree.
/// </para>
/// <para>
/// What has to come off first is everything about <i>where</i> the root stands, and it arrives in
/// two forms that look nothing alike in the source and identical to the emitter.
/// </para>
/// <list type="number">
/// <item>
/// <b>On the spine.</b> <c>object { pawn(f, rank) material: ivory }</c> binds to a union of one
/// carrying the material, wrapping a lathe carrying the <c>translate:</c>. Walking down
/// single-operand nodes takes both off, and what is left is a lathe at the origin.
/// </item>
/// <item>
/// <b>Inside the primitives.</b> <c>sphere { center: p }</c>, <c>box { min: … max: … }</c> and
/// <c>cylinder { base: … cap: … }</c> put the position in the solid's own fields, and this is the
/// commoner idiom by far — it is how every square of a chessboard and every cell of a lattice is
/// written. Nothing on the spine comes off, because there is nothing on the spine. So the shape
/// is emitted once to find out where its first leaf lands, and re-emitted normalised against
/// that.
/// </item>
/// </list>
/// <para>
/// Both steps are <b>exact</b>. The spine transform is removed rather than divided out, and the
/// second normalisation is a pure translation, which cancels against its own negation to the last
/// bit where a general matrix inverse would not. Nothing here rounds and nothing needs a
/// tolerance, so two appearances either agree completely or are honestly different.
/// </para>
/// <para>
/// Materials are excluded and travel with the placement instead. A leaf records a <i>slot</i>; an
/// appearance supplies what fills it. Without this an ivory pawn and an obsidian one would be two
/// shapes, which is exactly the sharing the work exists to get.
/// </para>
/// </remarks>
public static class ShapeCanonicalizer
{
    public static ShapePartition Partition(IReadOnlyList<Solid> roots)
    {
        Dictionary<string, ShapeGroup> byKey = [];
        List<ShapeGroup> groups = [];

        foreach (Solid root in roots)
        {
            Solid shape = Peel(root, out Matrix4x4 spine, out Material? inherited);

            var materials = new List<Material>();
            var slots = new List<int>();
            Resolve(shape, inherited, materials, slots, isShapeRoot: true);

            // Emitted once at the origin to find where its first leaf lands, then again with that
            // taken off. The second signature is the one two roots have to agree on.
            string? key = GeometryEmitter.Probe(shape, Matrix4x4.Identity, slots, out Matrix4x4 first);
            Matrix4x4 frame = Matrix4x4.CreateTranslation(-first.Translation);

            if (key is not null)
            {
                key = GeometryEmitter.Probe(shape, frame, slots, out _);
            }

            Matrix4x4 placement = Matrix4x4.CreateTranslation(first.Translation) * spine;
            bool shareable = key is not null && Shareable(shape, placement);

            if (!shareable || !byKey.TryGetValue(key!, out ShapeGroup? group))
            {
                group = new ShapeGroup
                {
                    Root = shape,
                    Key = key ?? string.Empty,
                    ShapeFrame = frame,
                    LeafSlots = slots,
                };

                groups.Add(group);

                if (shareable)
                {
                    byKey[key!] = group;
                }
            }

            group.Placements.Add(new ShapePlacement(shape, placement, spine, materials));
        }

        return new ShapePartition { Shapes = groups };
    }

    /// <summary>
    /// Walks the spine of single-operand nodes off the top of a root, gathering what belongs to
    /// the appearance rather than to the shape.
    /// </summary>
    private static Solid Peel(Solid root, out Matrix4x4 placement, out Material? inherited)
    {
        placement = Matrix4x4.Identity;
        inherited = null;

        Solid current = root;

        while (true)
        {
            // Row-vector convention, matching GeometryEmitter.Descend: the child multiplies on
            // the left, so a point in the shape's space is transformed by the shape's own node
            // first and by the outermost wrapper last.
            placement = current.Transform.Matrix * placement;
            inherited = current.Material ?? inherited;

            if (current is CsgOperation { Operands.Count: 1 } wrapper)
            {
                current = wrapper.Operands[0];
                continue;
            }

            return current;
        }
    }

    /// <summary>Whether a shape may be reached through the instance buffer at all.</summary>
    /// <remarks>
    /// <para>
    /// A subtree holding a <c>plane</c> has no bounding box — it is a half-space and genuinely
    /// infinite — and a BVH is built out of boxes, so it stays a singleton and keeps the
    /// unguarded block it has always had.
    /// </para>
    /// <para>
    /// A placement that mirrors is excluded for a different and more honest reason: a negative
    /// determinant reverses surface orientation, which meets <c>Hit.flip</c> and the
    /// entering/leaving rule, and there is no scene in the repository that exercises it. As a
    /// singleton it behaves exactly as it does today, which cannot be a regression. Sharing it
    /// would be a claim nothing has checked.
    /// </para>
    /// </remarks>
    private static bool Shareable(Solid shape, Matrix4x4 placement) =>
        placement.GetDeterminant() > 0f && !ContainsPlane(shape);

    private static bool ContainsPlane(Solid solid) => solid switch
    {
        Plane => true,
        CsgOperation operation => operation.Operands.Any(ContainsPlane),
        _ => false,
    };

    /// <summary>
    /// Settles which material slot each leaf wears, and what this appearance fills them with.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Slots are assigned by value in the order the leaves are reached, so a piece whose body and
    /// whose cross are the same material spends one slot rather than two. What makes two shapes
    /// equal is the <i>pattern</i> — which is in the signature, because the emitter writes the
    /// slot into each leaf's record — and never the values, which are the appearance's business.
    /// </para>
    /// <para>
    /// Leaves are reached in the order <see cref="GeometryEmitter"/> reaches them, operands left
    /// to right, because the emitter consumes this list by ordinal on the way out. That is the
    /// one thing the two walks have to agree about, and it is the reason material resolution
    /// lives here alone rather than in both.
    /// </para>
    /// </remarks>
    private static void Resolve(
        Solid solid,
        Material? inherited,
        List<Material> materials,
        List<int> slots,
        bool isShapeRoot = false)
    {
        // The shape root's own material was taken off with the spine, so it belongs to the
        // appearance; below it, a declared material belongs to the shape's structure.
        Material? effective = isShapeRoot ? inherited : solid.Material ?? inherited;

        if (solid is CsgOperation operation)
        {
            foreach (Solid operand in operation.Operands)
            {
                Resolve(operand, effective, materials, slots);
            }

            return;
        }

        Material material = effective ?? Material.Default;
        int slot = materials.IndexOf(material);

        if (slot < 0)
        {
            slot = materials.Count;
            materials.Add(material);
        }

        slots.Add(slot);
    }
}
