namespace Chroma.Core.Sdl.Binding;

/// <summary>
/// The arithmetic behind <c>random</c> and <c>perlin</c>: pure functions of a scene's seed
/// and their own arguments, drawn while the scene is being built.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing here is a stream.</b> There is no state to advance, so no result depends on the
/// order <see cref="Evaluator"/> happens to walk the tree — which is the property that lets
/// the evaluator be refactored without silently redrawing every scene that used these. The
/// scene supplies what varies, usually a loop counter, and gets the same number back for it
/// every time.
/// </para>
/// <para>
/// <b>Every step is exactly reproducible.</b> The same file has to give the same image on
/// another machine, so this uses integer mixing and the four IEEE 754 operations that are
/// correctly rounded, and nothing else. In particular there is no <c>sin</c> or <c>cos</c>
/// anywhere: the framework does not promise those to the last bit across platforms, and one
/// bit is a different gradient and a different landscape. For the same reason none of this
/// touches <see cref="System.Random"/>, whose sequence is documented as not being stable
/// across runtimes.
/// </para>
/// <para>
/// This is not the shader's hash. The shader draws a fresh number per pixel per bounce and
/// averaging those samples is what the image is; these are drawn once, before a shader
/// exists, and the compiler never learns that a radius was drawn rather than typed.
/// </para>
/// </remarks>
internal static class SceneNoise
{
    /// <summary>Eight unit gradients, as x and y interleaved.</summary>
    /// <remarks>
    /// Unit length is what makes the bound below exact, and the four diagonals are written as
    /// the literal decimal expansion of 1/sqrt(2) rather than computed, so that the table is
    /// the same bytes wherever it is built.
    /// </remarks>
    private static readonly double[] Gradients =
    [
        1.0, 0.0,
        -1.0, 0.0,
        0.0, 1.0,
        0.0, -1.0,
        0.7071067811865476, 0.7071067811865476,
        -0.7071067811865476, 0.7071067811865476,
        0.7071067811865476, -0.7071067811865476,
        -0.7071067811865476, -0.7071067811865476,
    ];

    /// <summary>
    /// Gradient noise in two dimensions is bounded by sqrt(2)/2 with unit gradients, so this
    /// is its reciprocal: one octave spans <c>[-1, 1]</c> and reaches both ends.
    /// </summary>
    private const double PerlinScale = 1.4142135623730951;

    /// <summary>A number in <c>[0, 1)</c>, a pure function of the seed and <paramref name="x"/>.</summary>
    public static double Random(int seed, double x) => ToUnitInterval(Hash(seed, Bits(x)));

    /// <summary>
    /// Coherent noise in <c>[-1, 1]</c>: neighbouring inputs give neighbouring outputs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One octave, and two dimensions.</b> A single octave looks like nothing anyone wants
    /// on its own, and fractal summation is a four-line loop in a language that has loops and
    /// arithmetic — so octaves, lacunarity and persistence are the scene's to write and are
    /// not hidden in here. Two dimensions rather than three because terrain is what a scene
    /// file asks noise for, and a solid texture belongs on the other side of the compiler,
    /// where it would be evaluated per hit rather than per field.
    /// </para>
    /// <para>
    /// This is Perlin's construction: fade, four corner gradients, bilinear blend. The
    /// gradients come from the same hash <see cref="Random"/> uses, so both functions answer
    /// to the scene's one seed.
    /// </para>
    /// </remarks>
    public static double Perlin(int seed, double x, double y)
    {
        // A coordinate too large to have a cell is a coordinate whose neighbours are not
        // distinguishable anyway, so it answers flat rather than wrapping into another part
        // of the field and pretending to be noise there.
        if (!IsCellable(x) || !IsCellable(y))
        {
            return 0.0;
        }

        long cellX = (long)Math.Floor(x);
        long cellY = (long)Math.Floor(y);

        double fx = x - cellX;
        double fy = y - cellY;

        double u = Fade(fx);
        double v = Fade(fy);

        double c00 = Corner(seed, cellX, cellY, fx, fy);
        double c10 = Corner(seed, cellX + 1, cellY, fx - 1.0, fy);
        double c01 = Corner(seed, cellX, cellY + 1, fx, fy - 1.0);
        double c11 = Corner(seed, cellX + 1, cellY + 1, fx - 1.0, fy - 1.0);

        double bottom = Lerp(c00, c10, u);
        double top = Lerp(c01, c11, u);

        return Lerp(bottom, top, v) * PerlinScale;
    }

    /// <summary>The lattice gradient at a corner, dotted with the offset to the sample.</summary>
    private static double Corner(int seed, long cellX, long cellY, double dx, double dy)
    {
        // The two coordinates are mixed one after the other rather than combined first, so
        // that (3, 4) and (4, 3) are different corners.
        ulong hash = Hash(seed, Hash(seed, (ulong)cellX) ^ (ulong)cellY);
        int index = (int)(hash & 7) * 2;

        return (Gradients[index] * dx) + (Gradients[index + 1] * dy);
    }

    /// <summary>Perlin's quintic fade, <c>6t^5 - 15t^4 + 10t^3</c>.</summary>
    /// <remarks>
    /// Quintic rather than the original cubic because its second derivative vanishes at both
    /// ends, which is what stops the lattice showing as a grid of creases once the noise is
    /// used as a height.
    /// </remarks>
    private static double Fade(double t) => t * t * t * ((t * ((t * 6.0) - 15.0)) + 10.0);

    private static double Lerp(double a, double b, double t) => a + (t * (b - a));

    /// <summary>Whether a coordinate still has a distinct integer cell under it.</summary>
    private static bool IsCellable(double value) =>
        !double.IsNaN(value) && Math.Abs(value) <= 9007199254740992.0;

    /// <summary>
    /// A double as the bits to hash, with the two zeros collapsed into one.
    /// </summary>
    /// <remarks>
    /// <c>-0.0</c> and <c>0.0</c> compare equal and have different bit patterns, and a scene
    /// that writes <c>random(-i)</c> at <c>i = 0</c> should not get a different number from
    /// one that writes <c>random(i)</c>.
    /// </remarks>
    private static ulong Bits(double value) =>
        BitConverter.DoubleToUInt64Bits(value == 0.0 ? 0.0 : value);

    /// <summary>
    /// The seed and a value, mixed into a number with no structure left in it.
    /// </summary>
    /// <remarks>
    /// SplitMix64's finaliser, twice: once over the seed so that adjacent seeds give
    /// unrelated fields, and once over the pair. Multiplication and shifts on a
    /// <see cref="ulong"/> are exact and wrap identically on every platform the renderer
    /// runs on, which is the whole reason the mixing happens in integers.
    /// </remarks>
    private static ulong Hash(int seed, ulong value) => Mix(value ^ Mix((ulong)(long)seed));

    private static ulong Mix(ulong z)
    {
        unchecked
        {
            z += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }

    /// <summary>
    /// The top 53 bits of a hash as a number in <c>[0, 1)</c>.
    /// </summary>
    /// <remarks>
    /// 53 because that is how many a double holds: taking more would divide by a power of two
    /// the mantissa cannot represent, and the rounding could land exactly on 1.0 — which the
    /// half-open interval promises never to happen.
    /// </remarks>
    private static double ToUnitInterval(ulong hash) => (hash >> 11) * (1.0 / 9007199254740992.0);
}
