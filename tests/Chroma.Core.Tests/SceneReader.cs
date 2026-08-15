using System.Numerics;
using Chroma.Core.Compilation;

namespace Chroma.Core.Tests;

/// <summary>
/// Reads a compiled scene back the way the shader would, for tests about how geometry is reached.
/// </summary>
/// <remarks>
/// Deliberately agnostic about chunks, cuts and sharing: it goes through the scene-wide tables and
/// the scene-wide shape ids, which are exactly the things a change to how a solid is reached could
/// get wrong. A test written against the emitted text instead would pass while the picture was
/// wrong.
/// </remarks>
internal static class SceneReader
{
    /// <summary>Where every leaf sits in world space, however it is reached.</summary>
    public static List<Vector3> LeafPositions(CompiledScene scene)
    {
        List<Vector3> positions = [];

        for (int leaf = 0; leaf < scene.PrimitiveCount; leaf++)
        {
            Matrix4x4 toLocal = MatrixAt(scene.Primitives, leaf * GpuLayout.PrimitiveStride);
            int shape = scene.LeafShapes[leaf];

            if (shape < 0)
            {
                positions.Add(OriginOf(toLocal));
                continue;
            }

            for (int instance = 0; instance < scene.InstanceCount; instance++)
            {
                int slot = instance * GpuLayout.InstanceStride;

                if ((int)scene.Instances[slot] != shape)
                {
                    continue;
                }

                positions.Add(OriginOf(MatrixAt(scene.Instances, slot) * toLocal));
            }
        }

        return positions;
    }

    /// <summary>
    /// Fails unless every solid in one compile stands where it stands in the other.
    /// </summary>
    /// <remarks>
    /// As sets rather than in order: neither chunking nor cutting promises to reach a scene's
    /// solids in the order it was written, and neither is allowed to move one.
    /// </remarks>
    public static void AssertSameSolids(CompiledScene expected, CompiledScene actual, string what)
    {
        List<Vector3> before = LeafPositions(expected);
        List<Vector3> after = LeafPositions(actual);

        Assert.Equal(before.Count, after.Count);

        foreach (Vector3 position in before)
        {
            Assert.True(
                after.Any(other => Vector3.Distance(position, other) < 1e-3f),
                $"a solid at {position} is nowhere near it once the scene is {what}");
        }
    }

    public static Matrix4x4 MatrixAt(IReadOnlyList<float> table, int header)
    {
        int m = header + 4;

        return new Matrix4x4(
            table[m], table[m + 1], table[m + 2], table[m + 3],
            table[m + 4], table[m + 5], table[m + 6], table[m + 7],
            table[m + 8], table[m + 9], table[m + 10], table[m + 11],
            table[m + 12], table[m + 13], table[m + 14], table[m + 15]);
    }

    public static Vector3 OriginOf(Matrix4x4 toLocal) =>
        Matrix4x4.Invert(toLocal, out Matrix4x4 toWorld) ? toWorld.Translation : Vector3.Zero;
}
