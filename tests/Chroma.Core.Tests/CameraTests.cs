using System.Numerics;
using Chroma.Core.Model;

namespace Chroma.Core.Tests;

public sealed class CameraTests
{
    [Fact]
    public void Looks_down_negative_z_from_positive_z()
    {
        Camera camera = new() { Position = new Vector3(0f, 0f, 5f), LookAt = Vector3.Zero };

        RayBasis basis = camera.CreateRayBasis(1f);

        AssertClose(new Vector3(0f, 0f, -1f), basis.Forward);

        // World +X on the right of the image. Getting this backwards mirrors every scene
        // with no error to explain it, which is exactly why it is asserted here.
        AssertClose(Vector3.UnitX, Vector3.Normalize(basis.Right));
        AssertClose(Vector3.UnitY, Vector3.Normalize(basis.Up));
    }

    [Fact]
    public void Mirrors_when_the_camera_is_placed_at_negative_z()
    {
        // Not a bug: looking the other way along an axis genuinely swaps left and right.
        // Pinned because it is the trap a POV-Ray habit walks into.
        Camera camera = new() { Position = new Vector3(0f, 0f, -5f), LookAt = Vector3.Zero };

        RayBasis basis = camera.CreateRayBasis(1f);

        AssertClose(-Vector3.UnitX, Vector3.Normalize(basis.Right));
    }

    [Fact]
    public void Produces_an_orthogonal_basis_from_a_skewed_up_vector()
    {
        // 'up' is only a roll reference and need not be perpendicular to the view.
        Camera camera = new()
        {
            Position = new Vector3(3f, 4f, 5f),
            LookAt = new Vector3(-1f, 0f, 2f),
            Up = new Vector3(0.2f, 1f, 0.3f),
        };

        RayBasis basis = camera.CreateRayBasis(1.5f);

        Assert.True(MathF.Abs(Vector3.Dot(basis.Forward, basis.Right)) < 1e-5f);
        Assert.True(MathF.Abs(Vector3.Dot(basis.Forward, basis.Up)) < 1e-5f);
        Assert.True(MathF.Abs(Vector3.Dot(basis.Right, basis.Up)) < 1e-5f);
        Assert.Equal(1f, basis.Forward.Length(), 5);
    }

    [Fact]
    public void Scales_up_by_half_the_vertical_field_of_view()
    {
        Camera camera = new()
        {
            Position = new Vector3(0f, 0f, 5f),
            LookAt = Vector3.Zero,
            FovDegrees = 90f,
        };

        RayBasis basis = camera.CreateRayBasis(1f);

        // tan(45 degrees) == 1, so the top edge of the image is exactly one unit up.
        Assert.Equal(1f, basis.Up.Length(), 5);
    }

    [Fact]
    public void Widens_right_by_the_aspect_ratio()
    {
        Camera camera = new() { Position = new Vector3(0f, 0f, 5f), LookAt = Vector3.Zero };

        RayBasis square = camera.CreateRayBasis(1f);
        RayBasis wide = camera.CreateRayBasis(2f);

        Assert.Equal(square.Up.Length(), wide.Up.Length(), 5);
        Assert.Equal(2f * square.Right.Length(), wide.Right.Length(), 5);
    }

    private static void AssertClose(Vector3 expected, Vector3 actual) =>
        Assert.True(Vector3.Distance(expected, actual) < 1e-5f, $"expected {expected}, got {actual}");
}
