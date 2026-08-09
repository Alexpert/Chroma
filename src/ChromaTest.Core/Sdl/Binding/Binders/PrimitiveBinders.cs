using System.Numerics;
using ChromaTest.Core.Model.Geometry;
using ChromaTest.Core.Model.Geometry.Primitives;

namespace ChromaTest.Core.Sdl.Binding.Binders;

public sealed class SphereBinder : SolidBinder
{
    public override string Name => "sphere";

    protected override Solid BindShape(BlockReader reader, BindingContext context) => new Sphere
    {
        Center = reader.Vector("center", Vector3.Zero),
        Radius = reader.Single("radius", 1f),
    };
}

public sealed class BoxBinder : SolidBinder
{
    public override string Name => "box";

    protected override Solid BindShape(BlockReader reader, BindingContext context)
    {
        Vector3 min = reader.Vector("min", new Vector3(-1f, -1f, -1f));
        Vector3 max = reader.Vector("max", new Vector3(1f, 1f, 1f));

        // Corners given the wrong way round would silently produce an empty solid, which
        // reads as "my box disappeared" rather than as a mistake in the file.
        if (min.X > max.X || min.Y > max.Y || min.Z > max.Z)
        {
            reader.Diagnostics.Error(
                reader.NameSpan,
                "'box' requires every component of 'min' to be less than or equal to 'max'");
        }

        return new Box { Min = min, Max = max };
    }
}

public sealed class CylinderBinder : SolidBinder
{
    public override string Name => "cylinder";

    protected override Solid BindShape(BlockReader reader, BindingContext context)
    {
        Vector3 basePoint = reader.Vector("base", Vector3.Zero);
        Vector3 cap = reader.Vector("cap", Vector3.UnitY);

        if (basePoint == cap)
        {
            reader.Diagnostics.Error(
                reader.NameSpan,
                "'cylinder' requires 'base' and 'cap' to be different points");
        }

        return new Cylinder
        {
            Base = basePoint,
            Cap = cap,
            Radius = reader.Single("radius", 1f),
        };
    }
}
