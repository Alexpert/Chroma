using System.Numerics;
using ChromaTest.Core.Model;
using ChromaTest.Core.Model.Lighting;
using ChromaTest.Core.Model.Materials;

namespace ChromaTest.Core.Sdl.Binding.Binders;

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

    public object Bind(BlockReader reader, BindingContext context) => new PointLight
    {
        Position = reader.RequireVector("position", Vector3.Zero),
        Color = reader.Vector("color", Vector3.One),
        Intensity = reader.Single("intensity", 1f),
    };
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

    public object Bind(BlockReader reader, BindingContext context) => new Material
    {
        Color = reader.Vector("color", new Vector3(0.8f, 0.8f, 0.8f)),
        Specular = reader.Single("specular", 0f),
        Shininess = reader.Single("shininess", 32f),
        Reflectivity = reader.Single("reflectivity", 0f),
        Name = reader.Block.SourceName,
    };
}
