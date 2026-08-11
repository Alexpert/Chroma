namespace Chroma.Core.Compilation;

/// <summary>
/// Primitive discriminator. These values are shared with the fragment shader's shading half
/// and must not be renumbered on one side alone.
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
    SphereSweep = 9,
}

/// <summary>
/// The layout of the two tables that survive code generation, in one place, because the packer
/// and the shader have to agree on it exactly and nothing checks that they do.
/// </summary>
/// <remarks>
/// This used to describe four buffers and hold the shader's array sizes as well. The tape and
/// the array sizes are gone: the tree is generated, and every array in the generated code is
/// sized from the node that owns it. What is left is the shading path's view of a leaf —
/// which kind it is, which material it wears, and how to get into its local space — and that
/// is unchanged.
/// </remarks>
public static class GpuLayout
{
    /// <summary>
    /// Floats per leaf: one texel of <c>(kind, materialIndex, paramA, paramB)</c> followed by
    /// the four rows of the inverse world-to-local matrix.
    /// </summary>
    /// <remarks>
    /// The two parameter slots hold a cone's taper and a torus's minor radius — the shapes no
    /// affine transform can absorb — and, for the four primitives defined by a list, an offset
    /// and a count into <see cref="ShapeStride"/>'s buffer. The generated span code needs none
    /// of it; only the normal does.
    /// </remarks>
    public const int PrimitiveStride = 5 * 4;

    /// <summary>
    /// Floats per texel of the shape buffer — the contour points, blob components and sweep
    /// spheres the normal path walks.
    /// </summary>
    public const int ShapeStride = 4;

    /// <summary>
    /// Floats per material, four texels:
    /// <c>(r, g, b, roughness)</c>,
    /// <c>(emissionR, emissionG, emissionB, metallic)</c>,
    /// <c>(absorptionR, absorptionG, absorptionB, transmission)</c>,
    /// <c>(ior, scattering, anisotropy, 0)</c>.
    /// </summary>
    /// <remarks>
    /// Each scalar rides in the alpha slot of a colour texel rather than taking one of its own.
    /// One float is still spare. Emission is a radiance and is deliberately not clamped.
    /// </remarks>
    public const int MaterialStride = 4 * 4;

    /// <summary>
    /// Points one <c>prism</c> or <c>lathe</c> outline may hold.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not a shader array size any more — the crossing array is generated at twice the segment
    /// count, which is the bound the old shared 32-slot array never was. What this bounds now
    /// is how much <b>source</b> one outline emits, and how wide a span list it forces on every
    /// operator above it. It is a generous sanity limit rather than a wall: <c>steps: 64</c> on
    /// a single Bézier curve fits inside it, which the old ceiling of 32 did not.
    /// </para>
    /// <para>
    /// The two limits below are still true array sizes, but of arrays in the generated code,
    /// sized per leaf. They are here because a scene that would make one absurd is better
    /// refused at its own field with a diagnostic than compiled into a shader the driver
    /// rejects.
    /// </para>
    /// </remarks>
    public const int MaxContourPoints = 64;

    /// <summary>Spheres one <c>sphereSweep</c> may hold.</summary>
    public const int MaxSweepSpheres = 32;

    /// <summary>Components one <c>blob</c> may hold.</summary>
    public const int MaxBlobComponents = 16;
}
