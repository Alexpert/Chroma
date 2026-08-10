namespace ChromaTest.Core.Compilation;

/// <summary>
/// Primitive discriminator. These values are shared with the fragment shader and must not
/// be renumbered on one side alone.
/// </summary>
public enum PrimitiveKind
{
    Sphere = 0,
    Box = 1,
    Cylinder = 2,
    Cone = 3,
    Plane = 4,
    Torus = 5,
    Prism = 6,
    Lathe = 7,
    Blob = 8,
}

/// <summary>
/// Tape instruction. <see cref="Leaf"/> pushes a primitive's spans, the three operators pop
/// two span lists and push their combination, and <see cref="EndRoot"/> pops one finished
/// root. Shared with the fragment shader.
/// </summary>
public enum TapeOpcode
{
    Leaf = 0,
    Union = 1,
    Intersection = 2,
    Difference = 3,

    /// <summary>
    /// Closes a root solid: the shader pops its list and folds the visible surface into
    /// the answer.
    /// </summary>
    /// <remarks>
    /// Top-level solids are implicitly unioned, but resolving them one at a time rather
    /// than merging them keeps the span budget a *per-root* limit. Merging instead would
    /// make a scene of nine separate spheres overflow a budget that comfortably renders a
    /// nine-way CSG tree, which is the wrong way round.
    /// </remarks>
    EndRoot = 4,
}

/// <summary>
/// The buffer layout, in one place, because the packer and the shader have to agree on it
/// exactly and nothing checks that they do.
/// </summary>
public static class GpuLayout
{
    /// <summary>Ints per tape instruction: opcode, primitive index, and two reserved.</summary>
    public const int TapeStride = 4;

    /// <summary>
    /// Floats per primitive: one texel of <c>(kind, materialIndex, paramA, paramB)</c>
    /// followed by the four rows of the inverse world-to-local matrix.
    /// </summary>
    /// <remarks>
    /// The two parameter slots were reserved from the start and stayed empty while every
    /// primitive was a fixed canonical shape. A cone's taper and a torus's minor radius are
    /// the first shapes that cannot be reached by any affine transform of a canonical form,
    /// so they live there; for the three primitives built from a list of points they hold
    /// instead an offset and a count into <see cref="ShapeStride"/>'s buffer.
    /// </remarks>
    public const int PrimitiveStride = 5 * 4;

    /// <summary>
    /// Floats per texel of the shape buffer — the variable-length data of a prism, a lathe
    /// or a blob, which does not fit the fixed primitive record.
    /// </summary>
    /// <remarks>
    /// A fourth texture buffer rather than a wider primitive record: a scene of spheres
    /// should not pay for the longest prism anyone might write, and the primitive record has
    /// to stay a fixed stride for <c>texelFetch</c> indexing to work at all.
    /// </remarks>
    public const int ShapeStride = 4;

    /// <summary>
    /// Floats per material, four texels:
    /// <c>(r, g, b, roughness)</c>,
    /// <c>(emissionR, emissionG, emissionB, metallic)</c>,
    /// <c>(absorptionR, absorptionG, absorptionB, transmission)</c>,
    /// <c>(ior, 0, 0, 0)</c>.
    /// </summary>
    /// <remarks>
    /// Each scalar rides in the alpha slot of a colour texel rather than taking one of its
    /// own. The three spare floats of the last texel are left spare: a scene holds a handful
    /// of materials, so the table's size is worth nothing next to being able to read it.
    /// Emission is a radiance and is deliberately not clamped.
    /// </remarks>
    public const int MaterialStride = 4 * 4;

    /// <summary>
    /// Spans one list can hold — <c>MAX_SPANS</c> in raytrace.frag.
    /// </summary>
    /// <remarks>
    /// GLSL 3.30 has no dynamically sized arrays, so the shader's limits are compile-time
    /// constants and the CPU has to know them to reject an oversized scene. Truncating a
    /// span list instead would produce geometry that is subtly wrong in a way that looks
    /// exactly like an algorithm bug, which is far more expensive to chase than an error
    /// message.
    /// </remarks>
    public const int MaxSpans = 8;

    /// <summary>Span lists held at once — <c>MAX_STACK</c> in raytrace.frag.</summary>
    public const int MaxStackDepth = 4;

    /// <summary>
    /// Instructions in one tape. Unlike the two above this is a CPU-side sanity cap rather
    /// than an array size: the tape lives in a buffer and the shader simply loops over it.
    /// It is here to keep a runaway scene from becoming a hung driver.
    /// </summary>
    public const int MaxInstructions = 256;

    /// <summary>
    /// Crossings one point-list primitive may produce along a ray — <c>MAX_CROSSINGS</c> in
    /// raytrace.frag, and twice <see cref="MaxSpans"/> because crossings pair into spans.
    /// </summary>
    public const int MaxCrossings = 2 * MaxSpans;

    /// <summary>
    /// Spans a primitive of this kind can produce along one ray.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Until this iteration every primitive was convex and the answer was always 1, so
    /// <c>SpanBudget.Leaf</c> was a constant. It no longer is: a ray through a torus's hole
    /// and out the other side crosses it twice, and a prism, a lathe or a blob is bounded by
    /// how many points or components it was given.
    /// </para>
    /// <para>
    /// Each bound is exact rather than generous. A ray crosses each extruded wall of a prism
    /// at most once, so <c>edges</c> crossings pair into <c>edges / 2</c> spans. Each band of
    /// a lathe can be crossed twice — once on the near side of the axis and once on the far
    /// side — giving <c>segments</c> spans. A blob's field is a sum of <c>n</c> single-humped
    /// bumps, which has at most <c>n</c> stretches above the threshold; a negative component
    /// can split one of those in two rather than adding a hump of its own, so <c>n</c> holds
    /// either way.
    /// </para>
    /// </remarks>
    public static int SpansFor(PrimitiveKind kind, int pointCount) => kind switch
    {
        PrimitiveKind.Torus => 2,
        PrimitiveKind.Prism => Math.Max(1, pointCount / 2),
        PrimitiveKind.Lathe => Math.Max(1, pointCount),
        PrimitiveKind.Blob => Math.Max(1, pointCount),
        _ => 1,
    };
}
