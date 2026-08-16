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
/// The layout of every table the shader reads, in one place, because the packer and the shader
/// have to agree on it exactly and nothing checks that they do.
/// </summary>
/// <remarks>
/// <para>
/// This once described four buffers and the shader's array sizes as well. The tape and the array
/// sizes went when the tree started being generated: every array in the generated code is sized
/// from the node that owns it. What was left was the shading path's view of a leaf: which kind
/// it is, which material it wears, and how to get into its local space.
/// </para>
/// <para>
/// Instancing adds two more, and they are read by the <b>span</b> path rather than the shading
/// one, which is the first time anything about a scene's shape has lived in memory since the tape
/// was deleted. The rule that decides where a number goes has not changed,
/// <i>constants for what is structural, buffers for what is repeated</i>, only which side of it
/// a placement falls on. See documents/gpu-backends.md.
/// </para>
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
    /// Floats per instance: one texel of <c>(shape, materialBase, 0, 0)</c> followed by the four
    /// rows of the world-to-shape matrix.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately the same shape as <see cref="PrimitiveStride"/>, so <c>fetchMatrix</c>'s
    /// convention, which is that the CPU writes rows, <c>mat4</c> takes columns, and the transpose that
    /// results is exactly what a row-vector matrix needs, is written down once and reused
    /// rather than restated.
    /// </para>
    /// <para>
    /// This is the table that makes a scene's size a property of memory instead of a property of
    /// the program. A placement here costs 80 bytes; the same placement folded into the source
    /// costs a copy of the whole shape in the driver's assembly. See documents/gpu-backends.md.
    /// </para>
    /// <para>
    /// <c>materialBase</c> is where this appearance's materials start. A leaf of a shared shape
    /// records a material <i>slot</i> rather than an index, because the body it is emitted in
    /// serves an ivory pawn and an obsidian one alike, and the two differ only here.
    /// </para>
    /// </remarks>
    public const int InstanceStride = 5 * 4;

    /// <summary>
    /// Floats per BVH node, two texels: <c>(min, escape)</c> and <c>(max, instance)</c>, where
    /// <c>instance</c> is -1 for an interior node.
    /// </summary>
    /// <remarks>
    /// The escape index is what lets the shader walk the tree with an <c>int</c> and no stack:
    /// see <see cref="InstanceBvh"/> for why that is worth a deeper tree.
    /// </remarks>
    public const int NodeStride = 2 * 4;

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
