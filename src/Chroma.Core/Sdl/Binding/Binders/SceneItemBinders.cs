using System.Numerics;
using Chroma.Core.Model;
using Chroma.Core.Model.Lighting;
using Chroma.Core.Model.Materials;

namespace Chroma.Core.Sdl.Binding.Binders;

public sealed class CameraBinder : INodeBinder
{
    public string Name => "camera";

    public object Bind(BlockReader reader, BindingContext context)
    {
        Vector3 position = reader.RequireVector("position", new Vector3(0f, 0f, 5f));
        Vector3 lookAt = reader.Vector("lookAt", Vector3.Zero);
        Vector3 up = reader.Vector("up", Vector3.UnitY);

        Vector3 forward = lookAt - position;

        if (forward.LengthSquared() < float.Epsilon)
        {
            reader.Diagnostics.Error(
                reader.NameSpan,
                "'camera' cannot look at its own position");
        }
        else if (Vector3.Cross(forward, up).LengthSquared() < 1e-10f)
        {
            // A parallel 'up' leaves the camera basis degenerate, which produces a blank
            // image rather than an error at render time.
            reader.Diagnostics.Error(
                reader.NameSpan,
                "'camera' requires 'up' not to be parallel to the direction of view");
        }

        return new Camera
        {
            Position = position,
            LookAt = lookAt,
            Up = up,
            FovDegrees = reader.Single("fov", 45f),
        };
    }
}

public sealed class PointLightBinder : INodeBinder
{
    public string Name => "pointLight";

    public object Bind(BlockReader reader, BindingContext context)
    {
        float radius = reader.Single("radius", 0f);

        if (radius < 0f)
        {
            reader.Diagnostics.Error(reader.NameSpan, "'pointLight' requires 'radius' to be zero or more");
            radius = 0f;
        }

        return new PointLight
        {
            Position = reader.RequireVector("position", Vector3.Zero),
            Color = reader.Vector("color", Vector3.One),
            Intensity = reader.Single("intensity", 1f),
            Radius = radius,
        };
    }
}

public sealed class DirectionalLightBinder : INodeBinder
{
    public string Name => "directionalLight";

    public object Bind(BlockReader reader, BindingContext context)
    {
        Vector3 direction = reader.RequireVector("direction", new Vector3(0f, -1f, 0f));

        if (direction.LengthSquared() < float.Epsilon)
        {
            reader.Diagnostics.Error(
                reader.NameSpan,
                "'directionalLight' requires a non-zero 'direction'");
            direction = new Vector3(0f, -1f, 0f);
        }

        return new DirectionalLight
        {
            // Normalised here so nothing downstream has to wonder whether it was.
            Direction = Vector3.Normalize(direction),
            Color = reader.Vector("color", Vector3.One),
            Intensity = reader.Single("intensity", 1f),
        };
    }
}

public sealed class MaterialBinder : INodeBinder
{
    public string Name => "material";

    public object Bind(BlockReader reader, BindingContext context)
    {
        // Clamped rather than reported: unlike a count, these are continuous quantities
        // where the intent of an out-of-range value is unambiguous.
        float metallic = Math.Clamp(reader.Single("metallic", 0f), 0f, 1f);
        float transmission = Math.Clamp(reader.Single("transmission", 0f), 0f, 1f);

        // Reported rather than clamped, because there is no sensible reading of either.
        // The surrounding medium is vacuum, so an index below 1 describes a solid thinner
        // than empty space; a negative extinction would create light rather than absorb it.
        float ior = reader.Single("ior", Material.Default.Ior);

        if (ior < 1f)
        {
            reader.Diagnostics.Error(
                reader.NameSpan,
                "'material' requires 'ior' to be 1 or more; the surrounding medium is vacuum");
            ior = Material.Default.Ior;
        }

        Vector3 absorption = reader.Vector("absorption", Vector3.Zero);

        if (absorption.X < 0f || absorption.Y < 0f || absorption.Z < 0f)
        {
            reader.Diagnostics.Error(
                reader.NameSpan,
                "'material' requires every component of 'absorption' to be zero or more");
            absorption = Vector3.Zero;
        }

        // A warning, not an error: the file still renders, and the shader gives a metal no
        // transmission lobe. Saying so is worth more than refusing the file, because the
        // mistake is invisible in the image -- the metal simply looks like a metal.
        if (metallic > 0f && transmission > 0f)
        {
            reader.Diagnostics.Warning(
                reader.NameSpan,
                "'material' sets both 'metallic' and 'transmission'; a metal does not "
                + "transmit, so 'transmission' is ignored here");
        }

        return new Material
        {
            Color = reader.Vector("color", new Vector3(0.8f, 0.8f, 0.8f)),
            Roughness = Math.Clamp(reader.Single("roughness", 0.5f), 0f, 1f),
            Metallic = metallic,

            // Not clamped: emission is radiance, not a colour, so values above 1 are ordinary.
            Emission = reader.Vector("emission", Vector3.Zero),

            Transmission = transmission,
            Ior = ior,
            Absorption = absorption,

            Name = reader.Block.SourceName,
        };
    }
}

public sealed class RenderBinder : INodeBinder
{
    public string Name => "render";

    public object Bind(BlockReader reader, BindingContext context) => new RenderSettings
    {
        MaxBounces = reader.Integer(
            "maxBounces",
            RenderSettings.Default.MaxBounces,
            RenderSettings.MinBounces,
            RenderSettings.MaxAllowedBounces),

        Exposure = reader.Single("exposure", RenderSettings.Default.Exposure),
    };
}
