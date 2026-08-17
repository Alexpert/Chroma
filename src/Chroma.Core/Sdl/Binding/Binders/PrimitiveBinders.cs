using System.Numerics;
using Chroma.Core.Assets;
using Chroma.Core.Compilation;
using Chroma.Core.Model.Geometry;
using Chroma.Core.Model.Geometry.Primitives;
using Chroma.Core.Sdl.Source;

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

        // 'spline' before 'steps' before the points, exactly as the lathe reads them, and for
        // the same reason: a file that gets both the spline name and the point count wrong
        // hears about the spline, which is the mistake that explains the other one.
        bool bezier = reader.Keyword("spline", "linear", "bezier") == 1;
        int steps = reader.Integer("steps", 8, 1, PointList.MaxSteps);

        ContourList? contours = bezier
            ? PointList.ReadBezier(reader, "points", steps, "x", "z")
            : PointList.Read(reader, "points", "x", "z");

        if (contours is null)
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

        return new Prism
        {
            Bottom = MathF.Min(bottom, top),
            Top = MathF.Max(bottom, top),
            Points = contours.Points,
            ContourSizes = contours.Sizes,
            Smooth = bezier,
        };
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

        ContourList? contours = bezier
            ? PointList.ReadBezier(reader, "points", steps, "radius", "y")
            : PointList.Read(reader, "points", "radius", "y");

        if (contours is null)
        {
            return null;
        }

        // A negative radius reflects that part of the outline through the axis, so the
        // surface of revolution crosses itself and stops bounding a solid.
        foreach (Vector2 point in contours.Points)
        {
            if (point.X < 0f)
            {
                reader.Diagnostics.Error(
                    reader.NameSpan,
                    "'lathe' requires every radius in 'points' to be zero or above");
                return null;
            }
        }

        return new Lathe
        {
            Points = contours.Points,
            ContourSizes = contours.Sizes,
            Smooth = bezier,
        };
    }
}

public sealed class SphereSweepBinder : SolidBinder
{
    /// <summary>Spheres needed for a path at all: one segment.</summary>
    private const int Minimum = 2;

    public override string Name => "sphereSweep";

    protected override Solid? BindShape(BlockReader reader, BindingContext context)
    {
        // 'spline' before 'steps' before the path, in the order the lathe and the prism read
        // theirs, and for the same reason.
        bool bezier = reader.Keyword("spline", "linear", "bezier") == 1;

        // Four, where an outline's default is eight. Each step of a path is a whole round cone
        // rather than a line segment, and the segment count is what the generated code unrolls.
        int steps = reader.Integer("steps", 4, 1, PointList.MaxSteps);

        List<Vector4>? spheres = bezier
            ? PointList.ReadBezierPath(reader, "spheres", steps)
            : ReadLinear(reader);

        if (spheres is null)
        {
            return null;
        }

        if (spheres.Count < Minimum)
        {
            reader.Diagnostics.Error(
                reader.NameSpan,
                $"'sphereSweep' needs at least {Minimum} spheres in 'spheres', "
                + $"found {spheres.Count}");
            return null;
        }

        // After flattening, not before: the cap is on what the generated code has to unroll,
        // and 'steps' is what decides that.
        if (spheres.Count > GpuLayout.MaxSweepSpheres)
        {
            reader.Diagnostics.Error(
                reader.NameSpan,
                $"'sphereSweep' has {spheres.Count} spheres"
                + (bezier ? " after flattening" : string.Empty)
                + $"; the limit is {GpuLayout.MaxSweepSpheres}"
                + (bezier ? ". Lower 'steps' or use fewer curves" : string.Empty));
            return null;
        }

        return new SphereSweep { Spheres = spheres };
    }

    private static List<Vector4>? ReadLinear(BlockReader reader)
    {
        IReadOnlyList<double>? numbers = reader.Components("spheres", groupOf: 4);

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
            if (!PointList.PositiveRadii(reader, "spheres", numbers, i))
            {
                return null;
            }

            spheres.Add(new Vector4(
                (float)numbers[i],
                (float)numbers[i + 1],
                (float)numbers[i + 2],
                (float)numbers[i + 3]));
        }

        return spheres;
    }
}

public sealed class QuadricBinder : SolidBinder
{
    public override string Name => "quadric";

    protected override Solid? BindShape(BlockReader reader, BindingContext context)
    {
        // Four named vectors rather than one run of ten numbers. Every other node here names
        // its vectors, and "field 'mixed' expects a vector of 3 components" is a message
        // someone can act on where "expected 10 numbers, found 9" is a counting exercise.
        // The defaults are the unit sphere, as 'sphere { }' is.
        Vector3 squared = reader.Vector("squared", Vector3.One);
        Vector3 mixed = reader.Vector("mixed", Vector3.Zero);
        Vector3 linear = reader.Vector("linear", Vector3.Zero);
        float constant = reader.Single("constant", -1f);

        // Every quadratic coefficient zero leaves a linear equation, whose solid is a
        // half-space. That is a 'plane', which is bounded properly and costs a third of this.
        if (squared == Vector3.Zero && mixed == Vector3.Zero)
        {
            reader.Diagnostics.Error(
                reader.NameSpan,
                "'quadric' needs a non-zero coefficient in 'squared' or 'mixed'; "
                + "with neither the surface is a plane, which 'plane' describes and bounds");
            return null;
        }

        return new Quadric
        {
            Squared = squared,
            Mixed = mixed,
            Linear = linear,
            Constant = constant,
        };
    }
}

/// <summary>
/// <c>mesh</c>: a triangle mesh from an <c>.obj</c> or <c>.stl</c> file.
/// </summary>
/// <remarks>
/// The only binder that reads a second file, and so the only one whose diagnostics can be about
/// something the scene does not contain. Three of them are possible and each points somewhere
/// different: the path field, when the file cannot be found or is not a format this reads; the
/// node name, when the file decodes but is not a solid; and the <c>maxSpans</c> field, when the
/// budget is out of range. The resolution rule is the evaluator's, from <c>import</c>: a relative
/// path is relative to the file that wrote it, so a folder of scenes beside a folder of models
/// keeps working wherever the renderer is run from.
/// </remarks>
public sealed class MeshBinder : SolidBinder
{
    /// <summary>
    /// Stretches of one ray that may lie inside a mesh, by default.
    /// </summary>
    /// <remarks>
    /// Four rather than one because a mesh is not convex and the interesting ones are not close
    /// to it: a ray through a teapot enters the body, leaves it, enters the spout and leaves
    /// again. Four rather than sixteen because a span list is state every thread carries, which
    /// is the one resource documents/csg-raytracing.md records as a wall rather than a slope.
    /// </remarks>
    private const int DefaultSpans = 4;

    public override string Name => "mesh";

    protected override Solid? BindShape(BlockReader reader, BindingContext context)
    {
        SourceSpan where = reader.Field("file")?.Value.Span ?? reader.NameSpan;
        string? written = reader.Text("file");

        // Read whatever else the block says before giving up on the path, so that a file naming
        // a missing model and a bad 'maxSpans' hears about both in one run.
        bool close = reader.Flag("close", false);
        bool smooth = reader.Flag("smooth", false);
        int maxSpans = reader.Integer("maxSpans", DefaultSpans, 1, ShapeCost.MaxSpans);

        if (written is null)
        {
            return null;
        }

        string? path = Resolve(written, where, reader);

        if (path is null)
        {
            return null;
        }

        MeshData? mesh = MeshFile.Load(path, close, smooth, out string? error);

        if (mesh is null)
        {
            reader.Diagnostics.Error(where, $"'{written}': {error}");
            return null;
        }

        if (mesh.TriangleCount > GpuLayout.MaxMeshTriangles)
        {
            reader.Diagnostics.Error(
                where,
                $"'{written}' holds {mesh.TriangleCount} triangles; "
                + $"the limit is {GpuLayout.MaxMeshTriangles}");
            return null;
        }

        return new Mesh
        {
            Positions = mesh.Positions,
            Indices = mesh.Indices,
            Normals = mesh.Normals,
            MaxSpans = maxSpans,
            Signature = MeshFile.Signature(mesh, smooth),
            Path = path,
        };
    }

    /// <summary>
    /// The path as written, against the directory of the file that wrote it.
    /// </summary>
    /// <remarks>
    /// The same rule and the same failure the evaluator applies to <c>import</c>, and for the
    /// same reason: resolving against the working directory would make a scene depend on where
    /// the renderer was started from. The scene file named on the command line stays the one
    /// exception, and it is resolved before any of this runs.
    /// </remarks>
    private static string? Resolve(string written, SourceSpan where, BlockReader reader)
    {
        string writer = where.Source?.Path ?? reader.Diagnostics.Source.Path;

        try
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(writer)) ?? string.Empty;
            return Path.GetFullPath(Path.Combine(directory, written));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            reader.Diagnostics.Error(where, $"'{written}' is not a usable file name");
            return null;
        }
    }
}

/// <summary>
/// <c>heightField</c>: a landscape the scene computes, as a solid.
/// </summary>
/// <remarks>
/// <para>
/// The only binder that runs the scene's own code. A grid of a million samples cannot be written
/// out as a literal, so the scene hands over a <i>function</i> and this calls it once per sample,
/// which is what <see cref="BindingContext.Call"/> exists for. The other form, <c>heights</c>,
/// takes the numbers directly and is what a small grid or an imported one is written as.
/// </para>
/// <para>
/// Exactly one of the two is required. Neither is not a shape, and both is two answers to one
/// question with no rule for which wins, so both are refused rather than one being preferred.
/// </para>
/// <para>
/// <b>The function is called with coordinates, not indices.</b> That is what makes
/// <c>resolution</c> say how finely rather than what shape: raising it refines the same terrain
/// instead of describing an unrelated one. It is the whole of why the field is worth having over
/// a grid the scene builds in a loop.
/// </para>
/// </remarks>
public sealed class HeightFieldBinder : SolidBinder
{
    /// <summary>
    /// Stretches of one ray that may lie inside a height field, by default.
    /// </summary>
    /// <remarks>
    /// Four, as a mesh's is, and for the same reason: the interesting cases are not convex. A ray
    /// low over a landscape passes through a ridge, out again, and into the next one.
    /// </remarks>
    private const int DefaultSpans = 4;

    /// <summary>
    /// Cells on a side when the scene does not say.
    /// </summary>
    /// <remarks>
    /// A hundred and twenty-eight is 16,641 samples, which loads in no time worth measuring and
    /// is enough to read as terrain rather than as a tent. The cap is eight times finer and is a
    /// deliberate choice by whoever writes it, since it is also a million calls of their function.
    /// </remarks>
    private const int DefaultResolution = 128;

    public override string Name => "heightField";

    protected override Solid? BindShape(BlockReader reader, BindingContext context)
    {
        BoundField? height = reader.Field("height");
        BoundField? heights = reader.Field("heights");

        // Read the rest before deciding anything, so that a block naming neither source and a
        // bad 'maxSpans' hears about both in one run. That is MeshBinder's rule. 'resolution' is
        // consumed here whether or not it turns out to be legal, so that a block refused below
        // does not also hear 'unknown field' from ReportUnusedEntries.
        BoundField? fineness = reader.Field("resolution");
        bool smooth = reader.Flag("smooth", false);
        int maxSpans = reader.Integer("maxSpans", DefaultSpans, 1, ShapeCost.MaxSpans);
        BoundField? floor = reader.Field("base");
        float floorAt = (float)reader.Number("base", 0d);

        int cells;
        List<float> samples;

        if (height is not null && heights is not null)
        {
            reader.Diagnostics.Error(
                heights.NameSpan,
                $"'{Name}' takes 'height' or 'heights', not both");

            return null;
        }

        if (height is null && heights is null)
        {
            reader.Diagnostics.Error(
                reader.NameSpan,
                $"'{Name}' requires 'height' or 'heights'; 'height' is a function of (x, z) "
                + "and 'heights' is a grid of numbers");

            return null;
        }

        if (height is not null)
        {
            int resolution = reader.Integer(
                "resolution", DefaultResolution, 1, GpuLayout.MaxHeightFieldResolution);

            if (Sample(height, resolution, reader, context) is not { } sampled)
            {
                return null;
            }

            cells = resolution;
            samples = sampled;
        }
        else
        {
            if (fineness is not null)
            {
                reader.Diagnostics.Error(
                    fineness.NameSpan,
                    "'resolution' says how finely 'height' is sampled and means nothing beside "
                    + "'heights', whose grid is its own resolution");

                return null;
            }

            if (Read(heights!, reader) is not { } grid)
            {
                return null;
            }

            cells = grid.Side - 1;
            samples = grid.Samples;
        }

        float low = samples[0];
        float high = samples[0];

        foreach (float sample in samples)
        {
            low = MathF.Min(low, sample);
            high = MathF.Max(high, sample);
        }

        if (floor is null)
        {
            // Strictly below the lowest sample, not level with it. A floor level with the
            // minimum leaves the solid zero-thickness wherever the terrain reaches it, and a
            // terrain with a flat bottom -- a plain, a sea bed, anything clamped -- reaches it
            // over an area rather than at a point. A ray entering there is neither inside nor
            // outside, the parity that defines the solid turns on a rounding, and the symptom is
            // a band of the surface that shadows itself. A hair of thickness removes the
            // question; the picture cannot show it.
            floorAt = low - (MathF.Max(high - low, 1f) * 1e-4f);
        }
        else if (floorAt >= high)
        {
            reader.Diagnostics.Error(
                floor.Value.Span,
                $"'base' is {Written(floorAt)} and the tallest sample is {Written(high)}, "
                + "which leaves no solid");

            return null;
        }

        return new HeightField
        {
            Heights = samples,
            Cells = cells,
            Base = floorAt,
            High = high,
            Smooth = smooth,
            MaxSpans = maxSpans,
            Signature = Signature(samples, cells, floorAt, smooth),
        };
    }

    /// <summary>
    /// The grid a scene function describes, or null having reported why it does not.
    /// </summary>
    /// <remarks>
    /// The two ends land exactly on -1 and +1 rather than at the end of an accumulated sum,
    /// because the footprint's edge is where the walls are and a sample half a step outside one
    /// would be a wall in the wrong place.
    /// </remarks>
    private static List<float>? Sample(
        BoundField height,
        int cells,
        BlockReader reader,
        BindingContext context)
    {
        SourceSpan where = height.Value.Span;

        int? parameters = height.Value switch
        {
            FunctionValue function => function.Parameters.Count,
            BuiltinValue builtin => builtin.Parameters.Count,
            _ => null,
        };

        if (parameters is null)
        {
            reader.Diagnostics.Error(
                where, $"field 'height' expects a function, found {height.Value.Describe()}");

            return null;
        }

        string name = height.Value switch
        {
            FunctionValue function => function.Name,
            _ => ((BuiltinValue)height.Value).Name,
        };

        if (parameters != 2)
        {
            reader.Diagnostics.Error(
                where,
                $"'{name}' takes {parameters} argument{(parameters == 1 ? string.Empty : "s")}; "
                + "'height' calls it with the x and z of each sample, which is 2");

            return null;
        }

        int side = cells + 1;

        // In double, like every other number the language computes with, so that a coordinate is
        // the same bits on every machine and 'perlin' gives the same terrain. The samples become
        // floats only when they are stored.
        double step = 2d / cells;
        List<float> samples = new(side * side);

        for (int j = 0; j < side; j++)
        {
            double z = j == cells ? 1d : -1d + (j * step);

            for (int i = 0; i < side; i++)
            {
                double x = i == cells ? 1d : -1d + (i * step);

                SdlValue[] arguments =
                [
                    new NumberValue(where, x),
                    new NumberValue(where, z),
                ];

                // A null result means the evaluator has already said why, so this adds nothing.
                if (context.Call(height.Value, arguments, where) is not { } result)
                {
                    return null;
                }

                if (result is not NumberValue number)
                {
                    reader.Diagnostics.Error(
                        where,
                        $"'{name}' returned {result.Describe()} at x = {Written(x)}, "
                        + $"z = {Written(z)}; 'height' has to return a number");

                    return null;
                }

                if (!double.IsFinite(number.Value))
                {
                    reader.Diagnostics.Error(
                        where,
                        $"'{name}' returned a value that is not finite at x = {Written(x)}, "
                        + $"z = {Written(z)}");

                    return null;
                }

                samples.Add((float)number.Value);
            }
        }

        return samples;
    }

    /// <summary>
    /// The grid a scene wrote out, flattened, with the side it turned out to be.
    /// </summary>
    private static (List<float> Samples, int Side)? Read(BoundField heights, BlockReader reader)
    {
        if (reader.Grid("heights") is not { } rows)
        {
            return null;
        }

        int side = rows.Count;

        if (side < 2)
        {
            reader.Diagnostics.Error(
                heights.Value.Span,
                $"field 'heights' needs at least 2 rows of 2, which is one cell; found {side}");

            return null;
        }

        if (rows[0].Count != side)
        {
            reader.Diagnostics.Error(
                heights.Value.Span,
                $"field 'heights' has {side} rows of {rows[0].Count}; the grid has to be square");

            return null;
        }

        if (side - 1 > GpuLayout.MaxHeightFieldResolution)
        {
            reader.Diagnostics.Error(
                heights.Value.Span,
                $"field 'heights' is {side} rows across; the limit is "
                + $"{GpuLayout.MaxHeightFieldResolution} cells, which is "
                + $"{GpuLayout.MaxHeightFieldResolution + 1} rows");

            return null;
        }

        List<float> samples = new(side * side);

        foreach (IReadOnlyList<double> row in rows)
        {
            foreach (double sample in row)
            {
                samples.Add((float)sample);
            }
        }

        return (samples, side);
    }

    /// <summary>
    /// What makes two height fields the same field.
    /// </summary>
    /// <remarks>
    /// Everything the packed block holds, and nothing else. The floor is in because it is in the
    /// header and it changes the solid; the smooth flag is in for the same reason. What is
    /// <b>not</b> in is how the grid was arrived at: the same numbers from a function and from a
    /// literal are the same field and should share one upload.
    /// </remarks>
    private static string Signature(IReadOnlyList<float> samples, int cells, float floor, bool smooth)
    {
        using ContentSignature signature = new();

        signature.Add(cells);
        signature.Add(samples);
        signature.Add(floor);
        signature.Add(smooth);

        return signature.ToString();
    }

    /// <summary>A number as a diagnostic should spell it, whatever the machine's culture is.</summary>
    private static string Written(double value) =>
        value.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
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

        // Both ends at one point: the distance to a segment of no length is the distance to
        // its point, so the one field formula covers a sphere with nothing special said.
        return new BlobComponent(center, center, radius, strength);
    }
}

public sealed class BlobCylinderBinder : INodeBinder
{
    public string Name => "blobCylinder";

    public object? Bind(BlockReader reader, BindingContext context)
    {
        Vector3 baseEnd = reader.Vector("base", Vector3.Zero);
        Vector3 cap = reader.Vector("cap", Vector3.UnitY);
        float radius = reader.Single("radius", 1f);
        float strength = reader.Single("strength", 1f);

        if (radius <= 0f)
        {
            reader.Diagnostics.Error(
                reader.NameSpan,
                "'blobCylinder' requires a 'radius' above 0");
            return null;
        }

        // Not a degenerate case to be repaired silently. A component with both ends at one
        // place is a sphere, and the file that wrote it either meant a sphere or mistyped one
        // of the two ends; saying which node it meant is more use than rendering a sphere.
        if (baseEnd == cap)
        {
            reader.Diagnostics.Error(
                reader.NameSpan,
                "'blobCylinder' requires 'base' and 'cap' to be different points; "
                + "a component with both ends at one place is a 'blobSphere'");
            return null;
        }

        return new BlobComponent(baseEnd, cap, radius, strength);
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

        List<BlobComponent> components = new(children.Count);

        foreach (SdlValue child in children)
        {
            switch (context.Bind(child, defaultTypeName: "blobSphere"))
            {
                case BlobComponent component:
                    components.Add(component);
                    break;

                case null:
                    break;

                default:
                    reader.Diagnostics.Error(
                        child.Span,
                        "'blob' takes 'blobSphere' and 'blobCylinder' components, "
                        + $"found {child.Describe()}");
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
/// One or more closed contours, as the concatenation of their points and the size of each.
/// </summary>
/// <remarks>
/// Concatenated rather than kept as a list of lists, because every consumer past the binder
/// walks all the points and only the two that close a contour or blend a normal across a joint
/// need to know where the seams are. <see cref="Sizes"/> is what those two read.
/// </remarks>
internal sealed record ContourList(IReadOnlyList<Vector2> Points, IReadOnlyList<int> Sizes);

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
    public static ContourList? ReadBezier(
        BlockReader reader,
        string field,
        int steps,
        string firstAxis,
        string secondAxis)
    {
        // Groups of two: a Bézier contour is a run of points like the linear form, read four
        // at a time rather than one. Writing it '[[x, z], …]' pairs the coordinates up; the
        // grouping into curves stays the field's own convention either way, and a second level
        // of nesting is a second contour.
        IReadOnlyList<IReadOnlyList<double>>? groups = reader.ComponentGroups(field, groupOf: 2);

        if (groups is null)
        {
            return null;
        }

        List<Vector2> points = [];
        List<int> sizes = new(groups.Count);

        for (int g = 0; g < groups.Count; g++)
        {
            IReadOnlyList<double> numbers = groups[g];

            if (numbers.Count % 8 != 0 || numbers.Count == 0)
            {
                reader.Diagnostics.Error(
                    reader.NameSpan,
                    $"'{reader.NodeName}' with 'spline: \"bezier\"' expects "
                    + $"{Which(g, groups.Count)}'{field}' to hold groups of four "
                    + $"({firstAxis}, {secondAxis}) points — eight numbers per curve — "
                    + $"found {numbers.Count} numbers");
                return null;
            }

            List<Vector2> contour = [];

            for (int at = 0; at < numbers.Count; at += 8)
            {
                Vector2 p0 = At(numbers, at);
                Vector2 p1 = At(numbers, at + 2);
                Vector2 p2 = At(numbers, at + 4);
                Vector2 p3 = At(numbers, at + 6);

                // From 1, not 0: the curve's first point is either the previous curve's last
                // or, for the very first curve, the one the closing edge comes back to.
                for (int step = 1; step <= steps; step++)
                {
                    contour.Add(CubicBezier(p0, p1, p2, p3, step / (float)steps));
                }
            }

            if (!Accept(reader, field, contour, points, sizes, g, groups.Count, flattened: true))
            {
                return null;
            }
        }

        return Whole(reader, field, points, sizes, flattened: true);
    }

    /// <summary>
    /// Closes one contour, checks it is big enough to bound an area, and adds it to the whole.
    /// </summary>
    private static bool Accept(
        BlockReader reader,
        string field,
        List<Vector2> contour,
        List<Vector2> points,
        List<int> sizes,
        int index,
        int count,
        bool flattened)
    {
        // The contour closes implicitly, so a file written in POV-Ray's style — which repeats
        // the first point to close a linear spline — would otherwise contribute a zero-length
        // edge. A Bézier chain that comes back exactly to its start does the same thing.
        if (contour.Count > 1 && contour[^1] == contour[0])
        {
            contour.RemoveAt(contour.Count - 1);
        }

        if (contour.Count < Minimum)
        {
            string after = flattened ? " after flattening" : string.Empty;

            reader.Diagnostics.Error(
                reader.NameSpan,
                $"'{reader.NodeName}' needs at least {Minimum} points in "
                + $"{Which(index, count)}'{field}'{after}, found {contour.Count}");
            return false;
        }

        points.AddRange(contour);
        sizes.Add(contour.Count);

        return true;
    }

    /// <summary>
    /// The one check that is about the solid rather than about a contour, and so is made once
    /// the last of them has been read.
    /// </summary>
    private static ContourList? Whole(
        BlockReader reader,
        string field,
        List<Vector2> points,
        List<int> sizes,
        bool flattened)
    {
        // Not a shader array size any more: the crossing array is generated at twice this
        // outline's own segment count, so nothing here can overflow it. What a very long
        // outline still costs is source — one line per edge — and a span list as wide as the
        // segment count on every operator above it, so it is bounded rather than unbounded.
        // A total rather than a per-contour cap, because both of those costs are totals.
        if (points.Count > GpuLayout.MaxContourPoints)
        {
            string after = flattened ? " after flattening" : string.Empty;

            reader.Diagnostics.Error(
                reader.NameSpan,
                $"'{reader.NodeName}' has {points.Count} points in '{field}'{after}; "
                + $"the limit is {GpuLayout.MaxContourPoints}"
                + (flattened ? ". Lower 'steps' or use fewer curves" : string.Empty));
            return null;
        }

        return new ContourList(points, sizes);
    }

    /// <summary>
    /// Names which contour a message is about, and says nothing at all when there is only one.
    /// </summary>
    private static string Which(int index, int count) =>
        count > 1 ? $"contour {index + 1} of " : string.Empty;

    /// <summary>
    /// Reads a <b>path</b> given as cubic Bézier curves and flattens it into a run of spheres.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A path is not a contour, which is why this is not <see cref="ReadBezier"/> with a
    /// different arity. It does not close, so its very first control point is a real point of
    /// the result rather than the one the closing edge comes back to; it must not drop a
    /// repeated last point, because repeating the first sphere is exactly how a sweep is made
    /// into a loop; and a curve therefore contributes <paramref name="steps"/> points to a
    /// total of <c>1 + curves * steps</c>.
    /// </para>
    /// <para>
    /// The radius is the fourth component of the same cubic, so a taper follows the curve
    /// instead of stepping at the joints. Checking the <b>control</b> radii is enough to know
    /// every flattened one is positive: a cubic Bézier stays inside the convex hull of its
    /// control points, so four positive radii cannot produce a negative one between them.
    /// </para>
    /// </remarks>
    public static List<Vector4>? ReadBezierPath(BlockReader reader, string field, int steps)
    {
        // Groups of four, as the linear form takes them: a curve is four of those, so sixteen
        // numbers, and the grouping into curves stays the field's own convention.
        IReadOnlyList<double>? numbers = reader.Components(field, groupOf: 4);

        if (numbers is null)
        {
            return null;
        }

        if (numbers.Count % 16 != 0 || numbers.Count == 0)
        {
            reader.Diagnostics.Error(
                reader.NameSpan,
                $"'{reader.NodeName}' with 'spline: \"bezier\"' expects '{field}' to hold "
                + "groups of four (x, y, z, radius) spheres — sixteen numbers per curve — "
                + $"found {numbers.Count} numbers");
            return null;
        }

        for (int i = 0; i < numbers.Count; i += 4)
        {
            if (!PositiveRadii(reader, field, numbers, i))
            {
                return null;
            }
        }

        List<Vector4> path = [At4(numbers, 0)];

        for (int at = 0; at < numbers.Count; at += 16)
        {
            Vector4 p0 = At4(numbers, at);
            Vector4 p1 = At4(numbers, at + 4);
            Vector4 p2 = At4(numbers, at + 8);
            Vector4 p3 = At4(numbers, at + 12);

            // From 1, not 0: this curve's start is either the previous curve's end or, for the
            // first curve, the point the path was seeded with above.
            for (int step = 1; step <= steps; step++)
            {
                path.Add(CubicBezier(p0, p1, p2, p3, step / (float)steps));
            }
        }

        return path;
    }

    /// <summary>
    /// Checks the radius of one <c>(x, y, z, radius)</c> group, whether it is a sphere of a
    /// linear path or a control point of a curved one.
    /// </summary>
    /// <remarks>
    /// A zero radius is refused along with a negative one. The sweep is the union of round
    /// cones between consecutive spheres, and a sphere of no size contributes a cone with a
    /// point on it — a surface with no thickness, which is not a boundary of anything.
    /// </remarks>
    public static bool PositiveRadii(
        BlockReader reader,
        string field,
        IReadOnlyList<double> numbers,
        int at)
    {
        if (numbers[at + 3] > 0d)
        {
            return true;
        }

        reader.Diagnostics.Error(
            reader.NameSpan,
            $"'{reader.NodeName}' requires every radius in '{field}' to be above 0");

        return false;
    }

    private static Vector2 At(IReadOnlyList<double> numbers, int index) =>
        new((float)numbers[index], (float)numbers[index + 1]);

    private static Vector4 At4(IReadOnlyList<double> numbers, int index) =>
        new(
            (float)numbers[index],
            (float)numbers[index + 1],
            (float)numbers[index + 2],
            (float)numbers[index + 3]);

    /// <summary>De Casteljau, written out — four points and one parameter.</summary>
    private static Vector2 CubicBezier(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float t)
    {
        float u = 1f - t;

        return (u * u * u * p0)
            + (3f * u * u * t * p1)
            + (3f * u * t * t * p2)
            + (t * t * t * p3);
    }

    /// <summary>
    /// The same cubic over four components, the fourth of which is a sweep's radius. Written
    /// twice rather than made generic: the body is one expression, and the alternative is an
    /// interface constraint that buys nothing.
    /// </summary>
    private static Vector4 CubicBezier(Vector4 p0, Vector4 p1, Vector4 p2, Vector4 p3, float t)
    {
        float u = 1f - t;

        return (u * u * u * p0)
            + (3f * u * u * t * p1)
            + (3f * u * t * t * p2)
            + (t * t * t * p3);
    }

    public static ContourList? Read(
        BlockReader reader,
        string field,
        string firstAxis,
        string secondAxis)
    {
        // '[[x0, z0], [x1, z1]]' or the flat '[x0, z0, x1, z1]' the language had before arrays
        // could nest. Both reach here as one run of numbers; a third level of brackets is
        // several runs, which is several contours.
        IReadOnlyList<IReadOnlyList<double>>? groups = reader.ComponentGroups(field, groupOf: 2);

        if (groups is null)
        {
            return null;
        }

        List<Vector2> points = [];
        List<int> sizes = new(groups.Count);

        for (int g = 0; g < groups.Count; g++)
        {
            IReadOnlyList<double> numbers = groups[g];

            if (numbers.Count % 2 != 0)
            {
                reader.Diagnostics.Error(
                    reader.NameSpan,
                    $"'{reader.NodeName}' expects {Which(g, groups.Count)}'{field}' to hold "
                    + $"pairs of ({firstAxis}, {secondAxis}) values, "
                    + $"found {numbers.Count} numbers");
                return null;
            }

            List<Vector2> contour = new(numbers.Count / 2);

            for (int i = 0; i < numbers.Count; i += 2)
            {
                contour.Add(new Vector2((float)numbers[i], (float)numbers[i + 1]));
            }

            if (!Accept(reader, field, contour, points, sizes, g, groups.Count, flattened: false))
            {
                return null;
            }
        }

        return Whole(reader, field, points, sizes, flattened: false);
    }
}
