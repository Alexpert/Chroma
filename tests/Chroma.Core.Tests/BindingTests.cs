using System.Numerics;
using Chroma.Core.Model;
using Chroma.Core.Model.Geometry;
using Chroma.Core.Model.Geometry.Operations;
using Chroma.Core.Model.Geometry.Primitives;
using Chroma.Core.Model.Lighting;
using Chroma.Core.Model.Materials;
using Chroma.Core.Sdl.Source;

namespace Chroma.Core.Tests;

public sealed class BindingTests
{
    [Fact]
    public void Applies_field_defaults()
    {
        Scene scene = TestSource.LoadValid("sphere { }");

        Sphere sphere = Assert.IsType<Sphere>(scene.Roots[0]);
        Assert.Equal(Vector3.Zero, sphere.Center);
        Assert.Equal(1f, sphere.Radius);
        Assert.Null(sphere.Material);
        Assert.True(sphere.Transform.IsIdentity);
    }

    [Fact]
    public void Binds_the_camera()
    {
        (Scene? scene, _) = TestSource.LoadRaw(
            "camera { position: [0, 2, -5], lookAt: [1, 0, 0], up: [0, 0, 1], fov: 60 }");

        Assert.NotNull(scene);
        Assert.Equal(new Vector3(0f, 2f, -5f), scene.Camera.Position);
        Assert.Equal(new Vector3(1f, 0f, 0f), scene.Camera.LookAt);
        Assert.Equal(new Vector3(0f, 0f, 1f), scene.Camera.Up);
        Assert.Equal(60f, scene.Camera.FovDegrees);
    }

    [Fact]
    public void Normalises_a_directional_light()
    {
        Scene scene = TestSource.LoadValid("directionalLight { direction: [0, -4, 0] }");

        DirectionalLight light = Assert.IsType<DirectionalLight>(Assert.Single(scene.Lights));
        Assert.Equal(new Vector3(0f, -1f, 0f), light.Direction);
    }

    [Fact]
    public void Accepts_an_anonymous_object_literal_for_a_material()
    {
        Scene scene = TestSource.LoadValid("sphere { material: { color: [1, 0, 0] } }");

        Assert.Equal(new Vector3(1f, 0f, 0f), scene.Roots[0].Material!.Color);
    }

    [Fact]
    public void Defaults_a_material_to_a_matte_dielectric()
    {
        Material material = TestSource.LoadValid("sphere { material: { } }").Roots[0].Material!;

        Assert.Equal(0.5f, material.Roughness);
        Assert.Equal(0f, material.Metallic);
        Assert.Equal(Vector3.Zero, material.Emission);
        Assert.Equal(0f, material.Transmission);
        Assert.Equal(Vector3.Zero, material.Absorption);
    }

    [Fact]
    public void Defaults_the_index_of_refraction_to_the_value_that_gives_four_percent()
    {
        // Not a placeholder. ((1.5 - 1) / (1.5 + 1))² is exactly 0.04, the reflectance
        // iteration 4 wrote as a constant for every dielectric — which is why adding the
        // field left every scene of that era rendering identically.
        Material material = TestSource.LoadValid("sphere { material: { } }").Roots[0].Material!;

        Assert.Equal(1.5f, material.Ior);

        float f0 = MathF.Pow((material.Ior - 1f) / (material.Ior + 1f), 2f);
        Assert.Equal(0.04f, f0, 5);
    }

    [Fact]
    public void Reads_the_transmissive_material_fields()
    {
        Material material = TestSource.LoadValid(
            "sphere { material: { transmission: 1, ior: 1.33, absorption: [0.2, 0.05, 0.4] } }")
            .Roots[0].Material!;

        Assert.Equal(1f, material.Transmission);
        Assert.Equal(1.33f, material.Ior);
        Assert.Equal(new Vector3(0.2f, 0.05f, 0.4f), material.Absorption);
    }

    [Theory]
    [InlineData("transmission: 4", 1f)]
    [InlineData("transmission: -1", 0f)]
    public void Clamps_transmission(string field, float expected)
    {
        Material material = TestSource.LoadValid($"sphere {{ material: {{ {field} }} }}")
            .Roots[0].Material!;

        Assert.Equal(expected, material.Transmission);
    }

    [Fact]
    public void Reports_an_index_of_refraction_below_one()
    {
        // The surrounding medium is vacuum, so a solid thinner than empty space has no
        // reading. Reported rather than clamped, because there is nothing to guess.
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) =
            TestSource.Load("sphere { material: { ior: 0.5 } }");

        Assert.Null(scene);
        Assert.Contains(diagnostics, d => d.Message.Contains("'ior' to be 1 or more"));
    }

    [Fact]
    public void Reports_a_negative_absorption()
    {
        // Negative extinction would create light along a path instead of removing it.
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) =
            TestSource.Load("sphere { material: { absorption: [0.1, -0.2, 0.1] } }");

        Assert.Null(scene);
        Assert.Contains(diagnostics, d => d.Message.Contains("'absorption' to be zero or more"));
    }

    [Fact]
    public void Warns_but_still_renders_a_metal_that_asks_to_transmit()
    {
        // A warning, not an error: the file is renderable and the picture is right. What is
        // wrong is the author's expectation, and nothing in the image would ever say so.
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) =
            TestSource.Load("sphere { material: { metallic: 1, transmission: 1 } }");

        Assert.NotNull(scene);
        Assert.Contains(
            diagnostics,
            d => d.Severity == DiagnosticSeverity.Warning
                && d.Message.Contains("a metal does not transmit"));
    }

    [Fact]
    public void Reads_the_medium_material_fields()
    {
        Material material = TestSource.LoadValid(
            """
            sphere {
              material: { transmission: 1, ior: 1, scattering: 0.4, anisotropy: 0.7 }
            }
            """)
            .Roots[0].Material!;

        Assert.Equal(0.4f, material.Scattering);
        Assert.Equal(0.7f, material.Anisotropy);
    }

    [Fact]
    public void Defaults_a_material_to_no_medium()
    {
        // Zero scattering is glass: a solid that absorbs along a straight line and never
        // redirects. Every scene written before iteration 10 has to keep meaning that.
        Material material = TestSource.LoadValid("sphere { material: { } }").Roots[0].Material!;

        Assert.Equal(0f, material.Scattering);
        Assert.Equal(0f, material.Anisotropy);
    }

    [Fact]
    public void Reports_a_negative_scattering()
    {
        // Same reading as a negative absorption: a negative rate would create light in the
        // volume rather than redirect what is already there.
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) =
            TestSource.Load("sphere { material: { transmission: 1, scattering: -0.5 } }");

        Assert.Null(scene);
        Assert.Contains(diagnostics, d => d.Message.Contains("'scattering' to be zero or more"));
    }

    [Theory]
    [InlineData("anisotropy: 3", 0.99f)]
    [InlineData("anisotropy: -3", -0.99f)]
    public void Clamps_anisotropy_inside_the_open_interval(string field, float expected)
    {
        // The interval is open, not closed: at exactly +/-1 the Henyey-Greenstein denominator
        // reaches zero at one end of the sphere and the phase function has no finite value
        // there. Clamping to 1 would put a division by zero one rounding error away.
        Material material = TestSource.LoadValid(
            $"sphere {{ material: {{ transmission: 1, scattering: 1, {field} }} }}")
            .Roots[0].Material!;

        Assert.Equal(expected, material.Anisotropy);
    }

    [Fact]
    public void Warns_but_still_renders_a_medium_in_an_opaque_solid()
    {
        // A warning for the same reason the metal one is: the file renders and the picture is
        // right. What is wrong is the expectation -- light never gets inside an opaque solid,
        // so the medium does nothing, and nothing in the image would ever say so.
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) =
            TestSource.Load("sphere { material: { scattering: 0.5 } }");

        Assert.NotNull(scene);
        Assert.Contains(
            diagnostics,
            d => d.Severity == DiagnosticSeverity.Warning
                && d.Message.Contains("light cannot enter an opaque solid"));
    }

    [Fact]
    public void Reads_the_pbr_material_fields()
    {
        Material material = TestSource.LoadValid(
            "sphere { material: { roughness: 0.2, metallic: 1, emission: [3, 4, 5] } }")
            .Roots[0].Material!;

        Assert.Equal(0.2f, material.Roughness);
        Assert.Equal(1f, material.Metallic);
        Assert.Equal(new Vector3(3f, 4f, 5f), material.Emission);
    }

    [Theory]
    [InlineData("roughness: 4", 1f, 0f)]
    [InlineData("roughness: -1", 0f, 0f)]
    [InlineData("metallic: 9", 0.5f, 1f)]
    public void Clamps_roughness_and_metallic(string field, float roughness, float metallic)
    {
        // Clamped rather than reported: unlike a bounce count, these are continuous
        // quantities where the intent of an out-of-range value is unambiguous.
        Material material = TestSource.LoadValid($"sphere {{ material: {{ {field} }} }}")
            .Roots[0].Material!;

        Assert.Equal(roughness, material.Roughness);
        Assert.Equal(metallic, material.Metallic);
    }

    [Fact]
    public void Defaults_a_point_light_to_a_zero_radius()
    {
        // Zero keeps the delta case, so every scene written before iteration 4 still has
        // exactly the hard shadows it had.
        Scene scene = TestSource.LoadValid("pointLight { position: [1, 2, 3] }");

        Assert.Equal(0f, Assert.IsType<PointLight>(Assert.Single(scene.Lights)).Radius);
    }

    [Fact]
    public void Reads_a_point_light_radius()
    {
        Scene scene = TestSource.LoadValid("pointLight { position: [1, 2, 3], radius: 0.75 }");

        Assert.Equal(0.75f, Assert.IsType<PointLight>(Assert.Single(scene.Lights)).Radius);
    }

    [Fact]
    public void Reports_a_negative_point_light_radius()
    {
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) =
            TestSource.Load("pointLight { position: [1, 2, 3], radius: -1 }");

        Assert.Null(scene);
        Assert.Contains(diagnostics, d => d.Message.Contains("'radius' to be zero or more"));
    }

    [Fact]
    public void Remembers_the_binding_name_of_a_material()
    {
        // Display only, but it is what turns the hierarchy dump from a wall of components
        // into something that reads like the file it came from.
        Scene scene = TestSource.LoadValid(
            "let red = material { color: [1, 0, 0] };\nsphere { material: red }");

        Assert.Equal("red", scene.Roots[0].Material!.Name);
    }

    [Fact]
    public void Records_a_material_only_where_it_is_declared()
    {
        // Inheritance down the tree is resolved when the scene is compiled for the GPU, so
        // at this stage a child that declares nothing must still report nothing.
        Scene scene = TestSource.LoadValid(
            """
            difference {
              box { }
              sphere { }
              material: { color: [1, 0, 0] }
            }
            """);

        Difference difference = Assert.IsType<Difference>(scene.Roots[0]);
        Assert.NotNull(difference.Material);
        Assert.All(difference.Operands, operand => Assert.Null(operand.Material));
    }

    [Fact]
    public void Applies_transforms_in_written_order()
    {
        // Translate-then-rotate swings the sphere around the origin; rotate-then-translate
        // leaves it where the translation put it. Anything that treats a block as an
        // unordered dictionary silently produces one when the file asks for the other.
        Scene translateFirst = TestSource.LoadValid(
            "sphere { translate: [2, 0, 0], rotate: [0, 90, 0] }");

        Scene rotateFirst = TestSource.LoadValid(
            "sphere { rotate: [0, 90, 0], translate: [2, 0, 0] }");

        Vector3 orbited = Vector3.Transform(Vector3.Zero, translateFirst.Roots[0].Transform.Matrix);
        Vector3 stayed = Vector3.Transform(Vector3.Zero, rotateFirst.Roots[0].Transform.Matrix);

        AssertClose(new Vector3(0f, 0f, -2f), orbited);
        AssertClose(new Vector3(2f, 0f, 0f), stayed);
    }

    [Fact]
    public void Keeps_transform_steps_as_written()
    {
        Scene scene = TestSource.LoadValid(
            "sphere { translate: [1, 0, 0], rotate: [0, 90, 0], scale: 2 }");

        Assert.Equal(
            [TransformKind.Translate, TransformKind.Rotate, TransformKind.Scale],
            scene.Roots[0].Transform.Steps.Select(s => s.Kind));
    }

    [Fact]
    public void Broadcasts_a_scalar_scale()
    {
        Scene scene = TestSource.LoadValid("sphere { scale: 3 }");

        Assert.Equal(new Vector3(3f, 3f, 3f), Assert.Single(scene.Roots[0].Transform.Steps).Value);
    }

    [Fact]
    public void Instantiates_a_referenced_solid_once_per_use()
    {
        // A binding stores the evaluated block, not a built solid, so each reference
        // produces an independent object that can carry its own placement.
        Scene scene = TestSource.LoadValid(
            """
            let unit = sphere { radius: 1 };
            union { unit, translate: [2, 0, 0] }
            union { unit }
            """);

        Solid first = Assert.IsType<Union>(scene.Roots[0]).Operands[0];
        Solid second = Assert.IsType<Union>(scene.Roots[1]).Operands[0];

        Assert.NotSame(first, second);
        Assert.True(first.Transform.IsIdentity);
        Assert.True(second.Transform.IsIdentity);
        Assert.Single(scene.Roots[0].Transform.Steps);
        Assert.True(scene.Roots[1].Transform.IsIdentity);
    }

    [Fact]
    public void Keeps_csg_operands_in_order()
    {
        Scene scene = TestSource.LoadValid(
            """
            difference {
              box { }
              sphere { }
              cylinder { }
            }
            """);

        Difference difference = Assert.IsType<Difference>(scene.Roots[0]);
        Assert.Collection(
            difference.Operands,
            operand => Assert.IsType<Box>(operand),
            operand => Assert.IsType<Sphere>(operand),
            operand => Assert.IsType<Cylinder>(operand));
    }

    [Fact]
    public void Collects_several_root_solids()
    {
        Scene scene = TestSource.LoadValid("sphere { }\nbox { }\ncylinder { }");

        Assert.Equal(3, scene.Roots.Count);
    }

    private static void AssertClose(Vector3 expected, Vector3 actual)
    {
        Assert.True(
            Vector3.Distance(expected, actual) < 1e-5f,
            $"expected {expected}, got {actual}");
    }
}
