using Chroma.Core.Compilation;
using Chroma.Core.Sdl.Source;

namespace Chroma.Core.Tests;

/// <summary>
/// That no tolerance in the renderer is a number somebody picked.
/// </summary>
/// <remarks>
/// <para>
/// Iteration 23 removed two absolute constants, <c>EPS</c> at 1e-4 and <c>SHADOW_BIAS</c> at
/// 1e-3, and replaced them with tolerances derived from the float format and from how far the
/// computed point actually missed its surface. Every one of those derivations lives in GLSL and
/// runs on the GPU, so what a test on this side can hold is the <b>seam</b>: that the emitter
/// still writes the derived form at every site, and that nothing has quietly reintroduced an
/// absolute one.
/// </para>
/// <para>
/// That is worth holding precisely because reintroducing one is easy and invisible. An absolute
/// tolerance produces a correct picture for every scene near the origin, which is every scene in
/// this repository; it fails at a distance, under a scaled instance, and inside a solid far
/// smaller than the scene around it, and none of those is what anybody renders while making the
/// change that breaks them. See documents/csg-raytracing.md.
/// </para>
/// </remarks>
public sealed class RoundingTests
{
    /// <summary>
    /// No emitted scene carries an absolute geometric tolerance.
    /// </summary>
    /// <remarks>
    /// Over every scene the repository ships rather than over one written here, because the sites
    /// differ by kind: a blob emits a breakpoint width test that a sphere does not, a lathe emits
    /// a segment test, and a scene of one sphere would exercise none of them.
    /// </remarks>
    [Theory]
    [MemberData(nameof(ShapeCostTests.RepositoryScenes), MemberType = typeof(ShapeCostTests))]
    public void No_scene_emits_an_absolute_tolerance(string name)
    {
        string geometry = Geometry(name);

        Assert.DoesNotMatch(@"\bEPS\b", geometry);
        Assert.DoesNotMatch(@"\bSHADOW_BIAS\b", geometry);
    }

    /// <summary>
    /// Every comparison the emitter makes on <c>t</c> is sized from the <c>t</c> it compares.
    /// </summary>
    /// <remarks>
    /// The count is not asserted, only that each shape of site is present: the number of them
    /// depends on how many roots and operators a scene needs, which is not what this is about.
    /// </remarks>
    [Theory]
    [MemberData(nameof(ShapeCostTests.RepositoryScenes), MemberType = typeof(ShapeCostTests))]
    public void Every_span_comparison_is_sized_from_the_t_it_compares(string name)
    {
        string geometry = Geometry(name);

        // The sliver guard, which every leaf and every operator goes through.
        Assert.Contains("sp_.tOut - sp_.tIn >= tTolerance(sp_.tOut)", geometry);

        // "Behind the eye", and "did the ray start inside this span" -- the pair that decides
        // which end of a span is the visible surface, and the one an absolute tolerance answered
        // differently for the same solid placed somewhere else.
        Assert.Contains("if (span.tOut < tTolerance(span.tOut)) continue;", geometry);
        Assert.Contains("if (span.tIn > tTolerance(span.tIn))", geometry);
    }

    /// <summary>
    /// The walk establishes the ray's own scale before anything reads it.
    /// </summary>
    /// <remarks>
    /// <c>gTScale</c> is a global for the reason every scratch array in the geometry path is one,
    /// which means nothing about its lifetime is enforced by the language. If a future
    /// <c>traceScene</c> ever read a tolerance before setting it, the tolerance would be whatever
    /// the previous ray left behind -- correct on most pixels, wrong on the ones where two rays
    /// differ most, and invisible in the source. So the order is asserted rather than assumed.
    /// </remarks>
    [Theory]
    [MemberData(nameof(ShapeCostTests.RepositoryScenes), MemberType = typeof(ShapeCostTests))]
    public void The_ray_scale_is_set_before_any_tolerance_reads_it(string name)
    {
        string geometry = Geometry(name);

        int walk = geometry.IndexOf(
            "Hit traceScene(vec3 ro, vec3 rd, bool anyHit, float maxT)", StringComparison.Ordinal);

        Assert.True(walk >= 0, $"{name} emitted no traceScene");

        int set = geometry.IndexOf("gTScale =", walk, StringComparison.Ordinal);
        int read = geometry.IndexOf("tTolerance(", walk, StringComparison.Ordinal);

        Assert.True(set >= 0, $"{name} never sets gTScale inside traceScene");
        Assert.True(
            read < 0 || set < read,
            $"{name} reads a tolerance at {read} before setting gTScale at {set}");
    }

    /// <summary>
    /// The hand-written half declares no absolute geometric constant either.
    /// </summary>
    /// <remarks>
    /// Read off the file rather than off a compiled scene, because this is the half the emitter
    /// splices into and the half a leftover constant would hide in. It is the one test here that
    /// reaches outside Chroma.Core, and it earns that by covering the shader's own preamble, the
    /// primitive normals and the two ray-spawning sites, none of which any scene's geometry
    /// contains.
    /// </remarks>
    [Fact]
    public void The_hand_written_shader_declares_no_absolute_geometric_constant()
    {
        string shader = File.ReadAllText(
            Path.Combine(RepositoryRoot(), "src", "Chroma", "Shaders", "raytrace.glsl"));

        // Declarations, not the prose above them: the comments explain at length what these were
        // and why they are gone, and a test that forbade the words would forbid the explanation.
        Assert.DoesNotMatch(@"const\s+float\s+EPS\b", shader);
        Assert.DoesNotMatch(@"const\s+float\s+SHADOW_BIAS\b", shader);

        // And the derivation itself is present, since "no constant" is also satisfied by a shader
        // that lost the tolerance altogether.
        Assert.Matches(@"float\s+tTolerance\(float t\)", shader);
        Assert.Matches(@"vec3\s+offsetOrigin\(", shader);
    }

    /// <summary>
    /// Every ray leaving a surface is offset by the measured bound, and none by a length.
    /// </summary>
    /// <remarks>
    /// There are three such sites and they used to write the offset out by hand, one of them with
    /// the sign reversed for a transmitted ray. Getting that sign wrong renders glass perfectly
    /// black, which reads as an absorption bug and is not one; routing all three through
    /// <c>offsetOrigin</c> is what stops them being able to disagree.
    /// </remarks>
    [Fact]
    public void Every_spawned_ray_starts_at_an_offset_that_was_measured()
    {
        string shader = File.ReadAllText(
            Path.Combine(RepositoryRoot(), "src", "Chroma", "Shaders", "raytrace.glsl"));

        Assert.Contains("offsetOrigin(v.point, v.error, v.axis, v.axis)", shader);
        Assert.Contains("offsetOrigin(v.point, v.error, v.axis, next)", shader);

        // Nothing multiplies the normal by a bare tolerance any more, which is the shape the two
        // hand-written offsets had.
        Assert.DoesNotMatch(@"v\.axis\s*\*\s*[A-Z_]+;", shader);
    }

    private static string Geometry(string name)
    {
        string path = Path.Combine(RepositoryRoot(), "scenes", name);

        bool ok = SceneLoader.TryLoadCompiled(
            path, out CompiledScene? compiled, out IReadOnlyList<Diagnostic> diagnostics);

        Assert.True(
            ok, $"failed to compile {name}: " + string.Join("; ", diagnostics.Select(d => d.Message)));

        return compiled!.Geometry;
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "scenes")))
        {
            directory = directory.Parent;
        }

        Assert.True(directory is not null, "could not find the repository root from the test binary");
        return directory!.FullName;
    }
}
