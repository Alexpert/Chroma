using System.Numerics;
using ChromaTest.Core.Model.Geometry;
using ChromaTest.Core.Model.Geometry.Primitives;

// System.Numerics has a Plane of its own — a mathematical plane, not a solid — and this file
// needs its vectors. The alias says which one is meant once, rather than at each mention.
using Plane = ChromaTest.Core.Model.Geometry.Primitives.Plane;

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

public sealed class ConeBinder : SolidBinder
{
    public override string Name => "cone";

    protected override Solid? BindShape(BlockReader reader, BindingContext context)
    {
        Vector3 basePoint = reader.Vector("base", Vector3.Zero);
        Vector3 cap = reader.Vector("cap", Vector3.UnitY);
        float baseRadius = reader.Single("baseRadius", 1f);
        float capRadius = reader.Single("capRadius", 0f);

        if (basePoint == cap)
        {
            reader.Diagnostics.Error(
                reader.NameSpan,
                "'cone' requires 'base' and 'cap' to be different points");
            return null;
        }

        if (baseRadius < 0f || capRadius < 0f)
        {
            reader.Diagnostics.Error(
                reader.NameSpan,
                "'cone' requires 'baseRadius' and 'capRadius' not to be negative");
            return null;
        }

        // Both radii zero is a line segment, not a solid, and it is the one combination that
        // would reach the compiler as a singular transform — a far less helpful message.
        if (baseRadius <= 0f && capRadius <= 0f)
        {
            reader.Diagnostics.Error(
                reader.NameSpan,
                "'cone' requires at least one of 'baseRadius' and 'capRadius' to be above 0");
            return null;
        }

        return new Cone
        {
            Base = basePoint,
            BaseRadius = baseRadius,
            Cap = cap,
            CapRadius = capRadius,
        };
    }
}

public sealed class PlaneBinder : SolidBinder
{
    public override string Name => "plane";

    protected override Solid? BindShape(BlockReader reader, BindingContext context)
    {
        Vector3 normal = reader.Vector("normal", Vector3.UnitY);
        float distance = reader.Single("distance", 0f);

        if (normal.LengthSquared() < 1e-12f)
        {
            reader.Diagnostics.Error(
                reader.NameSpan,
                "'plane' requires a 'normal' that is not the zero vector");
            return null;
        }

        return new Plane { Normal = normal, Distance = distance };
    }
}

public sealed class TorusBinder : SolidBinder
{
    public override string Name => "torus";

    protected override Solid? BindShape(BlockReader reader, BindingContext context)
    {
        Vector3 center = reader.Vector("center", Vector3.Zero);
        float major = reader.Single("majorRadius", 1f);
        float minor = reader.Single("minorRadius", 0.25f);

        if (major <= 0f || minor <= 0f)
        {
            reader.Diagnostics.Error(
                reader.NameSpan,
                "'torus' requires 'majorRadius' and 'minorRadius' to be above 0");
            return null;
        }

        // POV-Ray draws a self-intersecting spindle here and offers four ways to interpret
        // its inside. None of them is a shape a CSG operand can be trusted to be, so this
        // refuses rather than picking one.
        if (minor >= major)
        {
            reader.Diagnostics.Error(
                reader.NameSpan,
                "'torus' requires 'minorRadius' to be smaller than 'majorRadius'; "
                + "a self-intersecting spindle torus is not supported");
            return null;
        }

        return new Torus { Center = center, MajorRadius = major, MinorRadius = minor };
    }
}

public sealed class PrismBinder : SolidBinder
{
    public override string Name => "prism";

    protected override Solid? BindShape(BlockReader reader, BindingContext context)
    {
        float bottom = reader.Single("bottom", 0f);
        float top = reader.Single("top", 1f);
        IReadOnlyList<Vector2>? points = PointList.Read(reader, "points", "x", "z");

        if (points is null)
        {
            return null;
        }

        if (bottom == top)
        {
            reader.Diagnostics.Error(
                reader.NameSpan,
                "'prism' requires 'bottom' and 'top' to differ");
            return null;
        }

        return new Prism { Bottom = MathF.Min(bottom, top), Top = MathF.Max(bottom, top), Points = points };
    }
}

public sealed class LatheBinder : SolidBinder
{
    public override string Name => "lathe";

    protected override Solid? BindShape(BlockReader reader, BindingContext context)
    {
        IReadOnlyList<Vector2>? points = PointList.Read(reader, "points", "radius", "y");

        if (points is null)
        {
            return null;
        }

        // A negative radius reflects that part of the outline through the axis, so the
        // surface of revolution crosses itself and stops bounding a solid.
        foreach (Vector2 point in points)
        {
            if (point.X < 0f)
            {
                reader.Diagnostics.Error(
                    reader.NameSpan,
                    "'lathe' requires every radius in 'points' to be zero or above");
                return null;
            }
        }

        return new Lathe { Points = points };
    }
}

public sealed class BlobSphereBinder : INodeBinder
{
    public string Name => "blobSphere";

    public object? Bind(BlockReader reader, BindingContext context)
    {
        Vector3 center = reader.Vector("center", Vector3.Zero);
        float radius = reader.Single("radius", 1f);
        float strength = reader.Single("strength", 1f);

        if (radius <= 0f)
        {
            reader.Diagnostics.Error(
                reader.NameSpan,
                "'blobSphere' requires a 'radius' above 0");
            return null;
        }

        return new BlobSphere(center, radius, strength);
    }
}

public sealed class BlobBinder : SolidBinder
{
    public override string Name => "blob";

    protected override Solid? BindShape(BlockReader reader, BindingContext context)
    {
        float threshold = reader.Single("threshold", 1f);
        IReadOnlyList<SdlValue> children = reader.Children();

        if (children.Count == 0)
        {
            reader.Diagnostics.Error(reader.NameSpan, "'blob' needs at least one component");
            return null;
        }

        // A threshold at or below zero is met everywhere the field is defined and beyond,
        // so the surface is not where the file thinks it is — it is nowhere.
        if (threshold <= 0f)
        {
            reader.Diagnostics.Error(
                reader.NameSpan,
                "'blob' requires a 'threshold' above 0");
            return null;
        }

        List<BlobSphere> components = new(children.Count);

        foreach (SdlValue child in children)
        {
            switch (context.Bind(child, defaultTypeName: "blobSphere"))
            {
                case BlobSphere component:
                    components.Add(component);
                    break;

                case null:
                    break;

                default:
                    reader.Diagnostics.Error(
                        child.Span,
                        $"'blob' takes 'blobSphere' components, found {child.Describe()}");
                    break;
            }
        }

        return components.Count == children.Count ? new Blob
        {
            Threshold = threshold,
            Components = components,
        } : null;
    }
}

/// <summary>
/// Reads an interleaved list of 2D points, shared by <c>prism</c> and <c>lathe</c>.
/// </summary>
/// <remarks>
/// The two axes are named by the caller so the diagnostics can say <c>x</c> and <c>z</c> for
/// a prism and <c>radius</c> and <c>y</c> for a lathe, which is the difference between an
/// error someone can act on and one they have to look up.
/// </remarks>
internal static class PointList
{
    /// <summary>Points needed to bound an area, and so to describe a solid at all.</summary>
    private const int Minimum = 3;

    public static IReadOnlyList<Vector2>? Read(
        BlockReader reader,
        string field,
        string firstAxis,
        string secondAxis)
    {
        IReadOnlyList<double>? numbers = reader.Components(field);

        if (numbers is null)
        {
            return null;
        }

        if (numbers.Count % 2 != 0)
        {
            reader.Diagnostics.Error(
                reader.NameSpan,
                $"'{reader.NodeName}' expects '{field}' to hold pairs of "
                + $"({firstAxis}, {secondAxis}) values, found {numbers.Count} numbers");
            return null;
        }

        List<Vector2> points = new(numbers.Count / 2);

        for (int i = 0; i < numbers.Count; i += 2)
        {
            points.Add(new Vector2((float)numbers[i], (float)numbers[i + 1]));
        }

        // The contour closes implicitly, so a file written in POV-Ray's style — which
        // repeats the first point to close a linear spline — would otherwise contribute a
        // zero-length edge. Accepting both spellings is cheaper than explaining one.
        if (points.Count > 1 && points[^1] == points[0])
        {
            points.RemoveAt(points.Count - 1);
        }

        if (points.Count < Minimum)
        {
            reader.Diagnostics.Error(
                reader.NameSpan,
                $"'{reader.NodeName}' needs at least {Minimum} points in '{field}', "
                + $"found {points.Count}");
            return null;
        }

        return points;
    }
}
