using System.Numerics;
using Chroma.Core.Compilation;
using Chroma.Core.Model;
using Chroma.Core.Model.Geometry;

namespace Chroma.Core.Tests;

/// <summary>
/// A negative <c>scale</c>, which mirrors a solid.
/// </summary>
/// <remarks>
/// <para>
/// The case was <b>known to be untested rather than known to work</b> until iteration 20:
/// <see cref="ShapeCanonicalizer"/> excludes a mirrored placement from instancing and says why
/// in as many words, that reversing surface orientation meets <c>Hit.flip</c> and the
/// entering/leaving rule, and that no scene in the repository exercised it.
/// </para>
/// <para>
/// <b>It works.</b> A chiral solid was placed twice, once scaled by <c>-1</c> in X, and
/// rendered: the two are exact mirror images, the concave cut surfaces are lit rather than
/// black, and the shadows mirror with them. That is the half no unit test can reach, because
/// it needs a GPU and an eye. What is left is what a test *can* pin, and it is what would have
/// to break first for the render to go wrong: the transform reaches the compiler with its
/// handedness reversed, and nothing collapses the mirrored copy onto the original.
/// </para>
/// </remarks>
public sealed class MirrorTests
{
    [Theory]
    [InlineData("[-1, 1, 1]")]
    [InlineData("[1, -1, 1]")]
    [InlineData("[1, 1, -1]")]
    public void A_negative_scale_reverses_the_handedness_of_the_transform(string scale)
    {
        // The determinant is the whole of what "mirrored" means to everything downstream, and
        // it is what ShapeCanonicalizer tests to keep a mirrored placement out of instancing.
        Scene scene = TestSource.LoadValid($"sphere {{ radius: 1, scale: {scale} }}");

        Assert.True(scene.Roots[0].Transform.Matrix.GetDeterminant() < 0f);
    }

    [Fact]
    public void Scaling_by_minus_one_twice_is_not_a_mirror()
    {
        // Two reflections compose back to a rotation, so this one has to come out positive:
        // a test that only ever saw one sign would pass on a determinant that was always
        // negative for any scale at all.
        Scene scene = TestSource.LoadValid("sphere { radius: 1, scale: [-1, -1, 1] }");

        Assert.True(scene.Roots[0].Transform.Matrix.GetDeterminant() > 0f);
    }

    [Fact]
    public void A_mirrored_copy_is_not_shared_with_the_original()
    {
        // Even with sharing forced on everything. The two placements describe the same
        // geometry and are still two shapes, because one of them reverses orientation and the
        // instancing path carries a placement matrix rather than a re-emitted body.
        CompiledScene scene = TestSource.CompileShared(
            "sphere { radius: 0.5, translate: [-2, 0, 0] }\n"
            + "sphere { radius: 0.5, scale: [-1, 1, 1], translate: [2, 0, 0] }");

        Assert.Equal(2, scene.ShapeCount);
    }

    [Fact]
    public void A_mirrored_solid_still_reaches_the_compiler()
    {
        // The refusal above is about sharing, not about the scene: a mirrored solid compiles,
        // and the render that was looked at is the proof it draws.
        CompiledScene scene = TestSource.CompileValid(
            "difference {\n"
            + "  box { min: [-1, -1, -1], max: [1, 1, 1] }\n"
            + "  sphere { center: [1, 1, 1], radius: 1 }\n"
            + "  scale: [-1, 1, 1]\n"
            + "}");

        Assert.Single(scene.Scene.Roots);
        Assert.True(scene.Scene.Roots[0].Transform.Matrix.GetDeterminant() < 0f);
    }

    [Fact]
    public void The_steps_are_kept_as_written_so_the_dump_can_show_them()
    {
        // The hierarchy dump prints what the file says rather than a matrix, which is what
        // makes a mirrored placement visible to a reader looking for one.
        Scene scene = TestSource.LoadValid("sphere { scale: [-1, 1, 1] }");

        TransformStep step = Assert.Single(scene.Roots[0].Transform.Steps);
        Assert.Equal(TransformKind.Scale, step.Kind);
        Assert.Equal(new Vector3(-1f, 1f, 1f), step.Value);
    }
}
