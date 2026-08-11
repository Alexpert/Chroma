using System.Numerics;
using Chroma.Core.Compilation;

namespace Chroma.Core.Tests;

/// <summary>
/// Bounding-box guards: the iteration 11 speed-up that skips a whole subtree a ray misses.
/// </summary>
/// <remarks>
/// <para>
/// A guard may be too large and may not be too small, and no render will tell you which way it
/// went: a box a hair too tight removes geometry from a scene that otherwise looks entirely
/// correct, and the missing piece is whatever the camera was not pointed at. So the tests that
/// matter here are the enclosure ones, and they check the boundary — a solid's own extremes,
/// not a point comfortably inside it.
/// </para>
/// <para>
/// The tape assertions are the other half. A guard carries an absolute jump target, which is
/// the only absolute index in the whole tape; if it lands one instruction out, the stack
/// machine pops a list nothing pushed.
/// </para>
/// </remarks>
public sealed class BoundsTests
{
    /// <summary>
    /// A scene long enough that guards are emitted at all.
    /// </summary>
    /// <remarks>
    /// <see cref="CsgTapeBuilder.GuardsPayFrom"/> is a whole-scene threshold, so a test that
    /// wants to see a guard has to write a scene over it. Forty cells of two spheres each is
    /// the shortest way to get there and still have something recognisable to assert about.
    /// </remarks>
    private const string GuardedScene = """
        for (i in 0..40) union {
            sphere { center: [i * 10, 0, 0], radius: 1 }
            sphere { center: [i * 10 + 2, 0, 0], radius: 1 }
        }
        """;

    [Fact]
    public void A_small_scene_carries_no_guards()
    {
        // The cost of a guard is not only the guard: it turns the branch on for the whole
        // shader, and a scene with nothing worth skipping pays that for no return.
        CompiledScene scene = TestSource.CompileValid("difference { box { } sphere { } }");

        Assert.False(scene.HasBounds);
        Assert.DoesNotContain(TapeOf(scene), instruction => instruction.Opcode == TapeOpcode.Bound);
    }

    [Fact]
    public void A_long_scene_guards_every_operator()
    {
        CompiledScene scene = TestSource.CompileValid(GuardedScene);

        Assert.True(scene.HasBounds);
        Assert.Equal(40, TapeOf(scene).Count(i => i.Opcode == TapeOpcode.Bound));
    }

    [Fact]
    public void A_leaf_is_never_guarded()
    {
        // Testing a box costs what evaluating the primitive costs, so guarding a leaf pays
        // twice to save once. Every guard here should sit against a union, never a sphere.
        CompiledScene scene = TestSource.CompileValid(
            GuardedScene + "\nfor (i in 0..40) sphere { center: [i * 10, 20, 0] }");

        (TapeOpcode Opcode, int Operand, int Extra)[] tape = [.. TapeOf(scene)];

        foreach (int at in Enumerable.Range(0, tape.Length).Where(i => tape[i].Opcode == TapeOpcode.Bound))
        {
            Assert.Equal(TapeOpcode.Leaf, tape[at + 1].Opcode);
            Assert.Equal(TapeOpcode.Leaf, tape[at + 2].Opcode);
            Assert.Equal(TapeOpcode.Union, tape[at + 3].Opcode);
        }
    }

    [Fact]
    public void A_guard_jumps_to_the_instruction_after_its_subtree()
    {
        // The one absolute index in the tape. A ray that misses the box pushes an empty list
        // and resumes here, so this landing exactly one instruction early or late unbalances
        // the span stack for every instruction after it.
        CompiledScene scene = TestSource.CompileValid(GuardedScene);

        (TapeOpcode Opcode, int Operand, int Extra)[] tape = [.. TapeOf(scene)];

        foreach (int at in Enumerable.Range(0, tape.Length).Where(i => tape[i].Opcode == TapeOpcode.Bound))
        {
            // guard, leaf, leaf, union -> resume at the EndRoot four along.
            Assert.Equal(at + 4, tape[at].Operand);
            Assert.Equal(TapeOpcode.EndRoot, tape[tape[at].Operand].Opcode);
        }
    }

    [Fact]
    public void A_guard_box_encloses_the_solid_it_guards()
    {
        // The first cell is two unit spheres at x = 0 and x = 2, so the union runs from
        // (-1, -1, -1) to (3, 1, 1). Asserting the exact extremes rather than a comfortable
        // margin: a box that is loose is merely slow, and one that is tight by a rounding
        // error is a solid with a slice missing.
        CompiledScene scene = TestSource.CompileValid(GuardedScene);

        (Vector3 Min, Vector3 Max) box = FirstGuardBox(scene);

        Assert.Equal(new Vector3(-1f, -1f, -1f), box.Min);
        Assert.Equal(new Vector3(3f, 1f, 1f), box.Max);
    }

    [Fact]
    public void A_rotated_solid_grows_its_box_rather_than_shrinking_it()
    {
        // A box turned 45 degrees no longer has its own corners as its extremes, and the
        // axis-aligned box around it must reach sqrt(2) rather than 1. Transforming Min and
        // Max alone -- the obvious shortcut -- gives 1 and quietly clips the corners off.
        CompiledScene scene = TestSource.CompileValid(
            """
            for (i in 0..40) union {
                box { min: [-1, -1, -1], max: [1, 1, 1], rotate: [0, 45, 0] }
                sphere { center: [0, 0, 0], radius: 0.1 }
            }
            """);

        (Vector3 Min, Vector3 Max) box = FirstGuardBox(scene);

        Assert.Equal(MathF.Sqrt(2f), box.Max.X, 3);
        Assert.Equal(MathF.Sqrt(2f), box.Max.Z, 3);
        Assert.Equal(-MathF.Sqrt(2f), box.Min.X, 3);
    }

    [Fact]
    public void A_difference_is_bounded_by_its_left_operand_alone()
    {
        // Removing material cannot push the result outside what it was cut from, so the
        // subtracted solid contributes nothing to the box however far away it reaches.
        CompiledScene scene = TestSource.CompileValid(
            """
            for (i in 0..40) difference {
                box { min: [-1, -1, -1], max: [1, 1, 1] }
                sphere { center: [50, 50, 50], radius: 1 }
            }
            """);

        (Vector3 Min, Vector3 Max) box = FirstGuardBox(scene);

        Assert.Equal(new Vector3(-1f, -1f, -1f), box.Min);
        Assert.Equal(new Vector3(1f, 1f, 1f), box.Max);
    }

    [Fact]
    public void An_intersection_is_bounded_by_the_overlap()
    {
        CompiledScene scene = TestSource.CompileValid(
            """
            for (i in 0..40) intersection {
                box { min: [-2, -2, -2], max: [1, 1, 1] }
                box { min: [0, 0, 0], max: [4, 4, 4] }
            }
            """);

        (Vector3 Min, Vector3 Max) box = FirstGuardBox(scene);

        Assert.Equal(Vector3.Zero, box.Min);
        Assert.Equal(Vector3.One, box.Max);
    }

    [Fact]
    public void A_subtree_holding_a_plane_gets_a_box_no_ray_can_miss()
    {
        // A half-space is genuinely unbounded, so the honest answer is a box that always
        // passes. The alternative -- a finite box around an infinite solid -- would cut the
        // floor off wherever the camera was not looking.
        CompiledScene scene = TestSource.CompileValid(
            """
            for (i in 0..40) union {
                plane { normal: [0, 1, 0] }
                sphere { center: [i * 10, 1, 0], radius: 1 }
            }
            """);

        (Vector3 Min, Vector3 Max) box = FirstGuardBox(scene);

        Assert.True(box.Min.X < -1e29f, "an unbounded subtree must reach the sentinel");
        Assert.True(box.Max.Y > 1e29f, "an unbounded subtree must reach the sentinel");
    }

    [Fact]
    public void Aabb_intersection_of_disjoint_boxes_is_empty()
    {
        // Not a degenerate result to be repaired into something harmless: two solids whose
        // boxes do not meet cannot intersect, and every ray should skip the subtree outright.
        Aabb left = new(new Vector3(0f), new Vector3(1f));
        Aabb right = new(new Vector3(5f), new Vector3(6f));

        Assert.True(Aabb.Intersect(left, right).IsEmpty);
        Assert.False(Aabb.Union(left, right).IsEmpty);
    }

    [Fact]
    public void Aabb_union_starts_from_empty_without_dragging_in_the_origin()
    {
        // Empty is inverted rather than zero-sized, which is what stops a union seeded with it
        // from stretching back to (0, 0, 0).
        Aabb far = new(new Vector3(10f), new Vector3(12f));

        Assert.Equal(far, Aabb.Union(Aabb.Empty, far));
    }

    /// <summary>The box the first guard in the tape points at.</summary>
    private static (Vector3 Min, Vector3 Max) FirstGuardBox(CompiledScene scene)
    {
        (TapeOpcode Opcode, int Operand, int Extra) guard =
            TapeOf(scene).First(instruction => instruction.Opcode == TapeOpcode.Bound);

        int at = guard.Extra * GpuLayout.ShapeStride;

        return (
            new Vector3(scene.Shapes[at], scene.Shapes[at + 1], scene.Shapes[at + 2]),
            new Vector3(scene.Shapes[at + 4], scene.Shapes[at + 5], scene.Shapes[at + 6]));
    }

    private static IEnumerable<(TapeOpcode Opcode, int Operand, int Extra)> TapeOf(CompiledScene scene)
    {
        for (int i = 0; i < scene.Tape.Length; i += GpuLayout.TapeStride)
        {
            yield return ((TapeOpcode)scene.Tape[i], scene.Tape[i + 1], scene.Tape[i + 2]);
        }
    }
}
