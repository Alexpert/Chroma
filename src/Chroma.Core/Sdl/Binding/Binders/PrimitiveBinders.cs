using System.Numerics;
using Chroma.Core.Compilation;
using Chroma.Core.Model.Geometry;
using Chroma.Core.Model.Geometry.Primitives;

// System.Numerics has a Plane of its own — a mathematical plane, not a solid — and this file
// needs its vectors. The alias says which one is meant once, rather than at each mention.
using Plane = Chroma.Core.Model.Geometry.Primitives.Plane;

namespace Chroma.Core.Sdl.Binding.Binders;

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
        // Order matters only for the diagnostics: reading 'spline' first means a file that
        // gets both the spline name and the point count wrong hears about the spline, which
        // is the mistake that explains the other one.
        bool bezier = reader.Keyword("spline", "linear", "bezier") == 1;
        int steps = reader.Integer("steps", 8, 1, PointList.MaxSteps);

        IReadOnlyList<Vector2>? points = bezier
            ? PointList.ReadBezier(reader, "points", steps, "radius", "y")
            : PointList.Read(reader, "points", "radius", "y");

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

        return new Lathe { Points = points, Smooth = bezier };
    }
}

public sealed class SphereSweepBinder : SolidBinder
{
    /// <summary>Spheres needed for a path at all: one segment.</summary>
    private const int Minimum = 2;

    public override string Name => "sphereSweep";

    protected override Solid? BindShape(BlockReader reader, BindingContext context)
    {
        IReadOnlyList<double>? numbers = reader.Components("spheres");

        if (numbers is null)
        {
            return null;
        }

        if (numbers.Count % 4 != 0)
        {
            reader.Diagnostics.Error(
                reader.NameSpan,
                "'sphereSweep' expects 'spheres' to hold groups of (x, y, z, radius) values, "
                + $"found {numbers.Count} numbers");
            return null;
        }

        List<Vector4> spheres = new(numbers.Count / 4);

        for (int i = 0; i < numbers.Count; i += 4)
        {
            if (numbers[i + 3] <= 0d)
            {
                reader.Diagnostics.Error(
                    reader.NameSpan,
                    "'sphereSweep' requires every radius in 'spheres' to be above 0");
                return null;
            }

            spheres.Add(new Vector4(
                (float)numbers[i],
                (float)numbers[i + 1],
                (float)numbers[i + 2],
                (float)numbers[i + 3]));
        }

        if (spheres.Count < Minimum)
        {
            reader.Diagnostics.Error(
                reader.NameSpan,
                $"'sphereSweep' needs at least {Minimum} spheres in 'spheres', "
                + $"found {spheres.Count}");
            return null;
        }

        if (spheres.Count > GpuLayout.MaxSweepSpheres)
        {
            reader.Diagnostics.Error(
                reader.NameSpan,
                $"'sphereSweep' has {spheres.Count} spheres; "
                + $"the limit is {GpuLayout.MaxSweepSpheres}");
            return null;
        }

        return new SphereSweep { Spheres = spheres };
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

        if (components.Count > GpuLayout.MaxBlobComponents)
        {
            reader.Diagnostics.Error(
                reader.NameSpan,
                $"'blob' has {components.Count} components; "
                + $"the limit is {GpuLayout.MaxBlobComponents}");
            return null;
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

    /// <summary>
    /// Subdivisions one Bézier curve may be flattened into.
    /// </summary>
    /// <remarks>
    /// Generous, because the cost of a fine tessellation is not what it looks like. Segments
    /// beyond a certain point add crossings, not spans — a vase resolves to one or two spans
    /// whether it is drawn with 6 segments or 60 — and crossings are the cheap resource. The
    /// real ceiling is <c>GpuLayout.MaxContourPoints</c>, and going past it is reported.
    /// </remarks>
    public const int MaxSteps = 64;

    /// <summary>
    /// Reads a contour given as cubic Bézier curves — groups of four points, as POV-Ray's
    /// <c>bezier_spline</c> takes them — and flattens it into a polyline.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Flattening happens here, in the binder, and nothing downstream ever learns that a curve
    /// was involved: the model, the compiler and the shader all see a polyline. That is the
    /// whole reason a curved lathe costs nothing on the GPU — it is the machinery that already
    /// existed, with more vertices.
    /// </para>
    /// <para>
    /// Each curve contributes <paramref name="steps"/> points: its intermediate ones and its
    /// end, but not its start, which the previous curve already supplied. The contour closes
    /// implicitly, exactly as the linear form does.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<Vector2>? ReadBezier(
        BlockReader reader,
        string field,
        int steps,
        string firstAxis,
        string secondAxis)
    {
        IReadOnlyList<double>? numbers = reader.Components(field);

        if (numbers is null)
        {
            return null;
        }

        if (numbers.Count % 8 != 0 || numbers.Count == 0)
        {
            reader.Diagnostics.Error(
                reader.NameSpan,
                $"'{reader.NodeName}' with 'spline: \"bezier\"' expects '{field}' to hold "
                + $"groups of four ({firstAxis}, {secondAxis}) points — eight numbers per "
                + $"curve — found {numbers.Count} numbers");
            return null;
        }

        List<Vector2> points = [];

        for (int at = 0; at < numbers.Count; at += 8)
        {
            Vector2 p0 = At(numbers, at);
            Vector2 p1 = At(numbers, at + 2);
            Vector2 p2 = At(numbers, at + 4);
            Vector2 p3 = At(numbers, at + 6);

            // From 1, not 0: the curve's first point is either the previous curve's last or,
            // for the very first curve, the one the closing edge comes back to.
            for (int step = 1; step <= steps; step++)
            {
                points.Add(CubicBezier(p0, p1, p2, p3, step / (float)steps));
            }
        }

        // A curve whose end is its own start, or a chain that closes exactly, would leave a
        // zero-length edge behind; the linear reader drops the same thing for the same reason.
        if (points.Count > 1 && points[^1] == points[0])
        {
            points.RemoveAt(points.Count - 1);
        }

        return Validate(reader, field, points, flattened: true);
    }

    /// <summary>
    /// The two checks every contour has to pass, whichever form it was written in.
    /// </summary>
    private static IReadOnlyList<Vector2>? Validate(
        BlockReader reader,
        string field,
        List<Vector2> points,
        bool flattened)
    {
        string after = flattened ? " after flattening" : string.Empty;

        if (points.Count < Minimum)
        {
            reader.Diagnostics.Error(
                reader.NameSpan,
                $"'{reader.NodeName}' needs at least {Minimum} points in '{field}'{after}, "
                + $"found {points.Count}");
            return null;
        }

        // Not a shader array size any more: the crossing array is generated at twice this
        // outline's own segment count, so nothing here can overflow it. What a very long
        // outline still costs is source — one line per edge — and a span list as wide as the
        // segment count on every operator above it, so it is bounded rather than unbounded.
        if (points.Count > GpuLayout.MaxContourPoints)
        {
            reader.Diagnostics.Error(
                reader.NameSpan,
                $"'{reader.NodeName}' has {points.Count} points in '{field}'{after}; "
                + $"the limit is {GpuLayout.MaxContourPoints}"
                + (flattened ? ". Lower 'steps' or use fewer curves" : string.Empty));
            return null;
        }

        return points;
    }

    private static Vector2 At(IReadOnlyList<double> numbers, int index) =>
        new((float)numbers[index], (float)numbers[index + 1]);

    /// <summary>De Casteljau, written out — four points and one parameter.</summary>
    private static Vector2 CubicBezier(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float t)
    {
        float u = 1f - t;

        return (u * u * u * p0)
            + (3f * u * u * t * p1)
            + (3f * u * t * t * p2)
            + (t * t * t * p3);
    }

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

        return Validate(reader, field, points, flattened: false);
    }
}
