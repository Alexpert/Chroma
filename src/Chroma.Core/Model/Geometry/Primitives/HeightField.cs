namespace Chroma.Core.Model.Geometry.Primitives;

/// <summary>
/// A landscape the scene computed: a square grid of heights over the footprint
/// <c>[-1, 1] x [-1, 1]</c>, as an ordinary CSG operand.
/// </summary>
/// <remarks>
/// <para>
/// The first primitive whose parameter is a <b>grid</b> rather than a handful of numbers, and the
/// second, after <see cref="Mesh"/>, whose geometry does not go into the source it emits. It is a
/// solid rather than a surface, for the reason iteration 6 refused POV-Ray's <c>open</c> cones:
/// a CSG operand needs a well-defined inside. So the shape is the volume <i>under</i> the
/// surface, walled at the footprint's edges and floored at <see cref="Base"/>, which is what
/// makes <c>difference { terrain, sphere }</c> a crater rather than a window.
/// </para>
/// <para>
/// <b>No canonical form is folded into the matrix.</b> The heights are the numbers the scene
/// wrote. Normalising them into POV-Ray's unit cube would need the grid's own extremes in the
/// transform, which makes two terrains of different amplitude render identically and turns a flat
/// field into a scale of zero. <see cref="Lathe"/>'s outline and a <see cref="Mesh"/>'s positions
/// are already treated this way and this follows them.
/// </para>
/// <para>
/// <see cref="MaxSpans"/> budgets rather than describes, exactly as a mesh's does. A height
/// field's true worst case is one span per cell the ray crosses, which is not a span-list width
/// any scene could afford, so the bound is declared. See documents/height-fields.md.
/// </para>
/// </remarks>
public sealed class HeightField : Solid
{
    /// <summary>
    /// <c>(Cells + 1)²</c> samples, row major with z outermost: <c>[j * (Cells + 1) + i]</c>.
    /// </summary>
    public required IReadOnlyList<float> Heights { get; init; }

    /// <summary>Cells on a side. The sample grid is one larger on each side.</summary>
    public required int Cells { get; init; }

    /// <summary>Where the floor sits, in local y.</summary>
    /// <remarks>
    /// Defaulted by the binder to the lowest sample rather than to zero, because <c>perlin</c> is
    /// signed: a floor at zero would silently cut away everything below it and the first terrain
    /// anyone writes would render in pieces. Written explicitly, it means what it says, and the
    /// solid is simply absent wherever the surface falls below it.
    /// </remarks>
    public required float Base { get; init; }

    /// <summary>The tallest sample, which is the top of the bounding box.</summary>
    /// <remarks>
    /// Carried rather than recomputed downstream: the emitter needs it for the leaf's bounds and
    /// the shader needs it for the clip that opens the march, and the two have to agree to the
    /// bit or a ray can enter the box above every triangle in it.
    /// </remarks>
    public required float High { get; init; }

    /// <summary>Whether to shade by normals interpolated across a cell.</summary>
    /// <remarks>
    /// Unlike a mesh's, these are stored nowhere. A height is a function of two coordinates, so
    /// the normal at a sample is a central difference over its four neighbours and the shader
    /// computes it at the hit. The flag only has to reach the shader, which is what the header
    /// lane is for.
    /// </remarks>
    public bool Smooth { get; init; }

    /// <summary>How many separate stretches of one ray may lie inside this field.</summary>
    public required int MaxSpans { get; init; }

    /// <summary>
    /// What makes two height fields the same field, from <c>ContentSignature</c>.
    /// </summary>
    /// <remarks>
    /// Carried for the reason a mesh carries one: the emitted body holds a buffer offset and
    /// nothing else, and inside a probe every buffer starts empty. Without this, two different
    /// terrains in one scene would compare equal and the second would be drawn as the first.
    /// </remarks>
    public required string Signature { get; init; }

    public int SampleCount => (Cells + 1) * (Cells + 1);

    public override string Kind => "HeightField";

    public override void Accept(ISolidVisitor visitor) => visitor.VisitHeightField(this);

    public override TResult Accept<TResult>(ISolidVisitor<TResult> visitor) =>
        visitor.VisitHeightField(this);
}
