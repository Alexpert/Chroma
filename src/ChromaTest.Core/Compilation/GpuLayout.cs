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
/// Tape instruction. <see cref="Leaf"/> pushes a primitive's spans; the others pop two
/// span lists and push their combination. Shared with the fragment shader.
/// </summary>
public enum TapeOpcode
{
    Leaf = 0,
    Union = 1,
    Intersection = 2,
    Difference = 3,
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
    /// Floats per material: (r, g, b, specular) then (shininess, reflectivity, 0, 0).
    /// </summary>
    public const int MaterialStride = 2 * 4;
}
