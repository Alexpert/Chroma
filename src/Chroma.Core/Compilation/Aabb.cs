using System.Numerics;

namespace Chroma.Core.Compilation;

/// <summary>
/// An axis-aligned box that encloses a subtree, in world space.
/// </summary>
/// <remarks>
/// <para>
/// This is the only approximate thing in the compiler, and it is approximate in one direction
/// only: a box may be larger than the solid it encloses, never smaller. Everything downstream
/// rests on that. A ray that misses the box skips the subtree without evaluating it, so a box
/// that is too small removes geometry from the image, while one that is too large only costs
/// time.
/// </para>
/// <para>
/// Boxes rather than spheres. A sphere is one texel to a box's two and its test is shorter,
/// but the shapes this renderer is made of are overwhelmingly axis-aligned or nearly so — a
/// lattice of struts, a room of walls — and a sphere around a thin diagonal strut encloses
/// eleven times the volume the box does. What is skipped matters more than what the test costs.
/// </para>
/// </remarks>
public readonly record struct Aabb(Vector3 Min, Vector3 Max)
{
    /// <summary>
    /// The box that contains everything, for a subtree no box can bound — one holding a
    /// <c>plane</c>, which is a half-space and genuinely infinite.
    /// </summary>
    /// <remarks>
    /// A finite sentinel rather than <see cref="float.PositiveInfinity"/>: it travels through
    /// the same slab test as every other box, and infinity there produces <c>inf - inf</c>
    /// for a ray parallel to a slab, which is NaN, and a NaN comparison is false — so the
    /// unbounded box would be the one box every ray missed. It matches <c>INF</c> in
    /// raytrace.frag.
    /// </remarks>
    public static readonly Aabb Unbounded = new(new Vector3(-1e30f), new Vector3(1e30f));

    /// <summary>
    /// A box containing nothing, so that <see cref="Union"/> can start somewhere.
    /// </summary>
    /// <remarks>Inverted rather than zero-sized: a zero-sized box at the origin would drag
    /// every union it took part in back to include the origin.</remarks>
    public static readonly Aabb Empty = new(new Vector3(float.PositiveInfinity), new Vector3(float.NegativeInfinity));

    /// <summary>True when this box encloses no point at all.</summary>
    /// <remarks>
    /// Not a degenerate case to be repaired. <c>intersection { a b }</c> of two solids whose
    /// boxes do not overlap is genuinely nothing, and the tape is entitled to say so — every
    /// ray then skips the subtree outright.
    /// </remarks>
    public bool IsEmpty => Min.X > Max.X || Min.Y > Max.Y || Min.Z > Max.Z;

    /// <summary>True when the box has real extents rather than a sentinel or an infinity.</summary>
    public bool IsFinite => Min.X > -1e29f && Min.Y > -1e29f && Min.Z > -1e29f
                         && Max.X < 1e29f && Max.Y < 1e29f && Max.Z < 1e29f;

    /// <summary>A box around a sphere.</summary>
    public static Aabb AroundSphere(Vector3 centre, float radius) =>
        new(centre - new Vector3(radius), centre + new Vector3(radius));

    public static Aabb Union(Aabb a, Aabb b) => new(Vector3.Min(a.Min, b.Min), Vector3.Max(a.Max, b.Max));

    /// <summary>
    /// The box around <c>a ∩ b</c>, which is the overlap of the two boxes.
    /// </summary>
    /// <remarks>
    /// May come out inverted, and that is the useful answer rather than a degenerate one: two
    /// solids whose boxes do not overlap cannot intersect, so the result is genuinely empty and
    /// every ray should skip it.
    /// </remarks>
    public static Aabb Intersect(Aabb a, Aabb b) => new(Vector3.Max(a.Min, b.Min), Vector3.Min(a.Max, b.Max));

    /// <summary>
    /// The box around this box's eight corners after <paramref name="transform"/>.
    /// </summary>
    /// <remarks>
    /// Eight corners rather than transforming <see cref="Min"/> and <see cref="Max"/> alone.
    /// Under a rotation those two points do not stay opposite, and a box built from them is
    /// smaller than the solid — which is the one error this type may not make. A rotated box
    /// grows in the process, and that looseness is the accepted price of keeping the test
    /// axis-aligned.
    /// </remarks>
    public Aabb Transformed(Matrix4x4 transform)
    {
        if (IsEmpty || !IsFinite)
        {
            // Nothing sensible to transform, and a matrix applied to the sentinel would
            // overflow it into infinity.
            return this;
        }

        Aabb result = Empty;

        for (int corner = 0; corner < 8; corner++)
        {
            Vector3 point = new(
                (corner & 1) == 0 ? Min.X : Max.X,
                (corner & 2) == 0 ? Min.Y : Max.Y,
                (corner & 4) == 0 ? Min.Z : Max.Z);

            point = Vector3.Transform(point, transform);
            result = new Aabb(Vector3.Min(result.Min, point), Vector3.Max(result.Max, point));
        }

        return result;
    }
}
