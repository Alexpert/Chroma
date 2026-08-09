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
    /// Floats per primitive: one texel of (kind, materialIndex, 0, 0) followed by the four
    /// rows of the inverse world-to-local matrix.
    /// </summary>
    public const int PrimitiveStride = 5 * 4;

    /// <summary>
    /// Floats per material: (r, g, b, roughness) then (emissionR, emissionG, emissionB,
    /// metallic).
    /// </summary>
    /// <remarks>
    /// The two scalars ride in the alpha slots of the two colour texels rather than taking
    /// a third texel of their own. Emission is a radiance and is deliberately not clamped.
    /// </remarks>
    public const int MaterialStride = 2 * 4;

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
}
