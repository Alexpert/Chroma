using System.Numerics;
using System.Text;
using ChromaTest.Core.Model;
using ChromaTest.Core.Model.Geometry;
using ChromaTest.Core.Model.Geometry.Operations;
using ChromaTest.Core.Model.Geometry.Primitives;
using ChromaTest.Core.Model.Lighting;
using ChromaTest.Core.Model.Materials;

// System.Numerics has a Plane of its own — a mathematical plane, not a solid — and this file
// needs its vectors. The alias says which one is meant once, rather than at each mention.
using Plane = ChromaTest.Core.Model.Geometry.Primitives.Plane;

namespace ChromaTest.SceneDump;

/// <summary>
/// Renders a loaded scene as an indented tree.
/// </summary>
/// <remarks>
/// Drawn with plain ASCII rather than Unicode box characters: under the classic Windows
/// console those depend on the active code page and turn into mojibake, and a diagnostic
/// tool that is unreadable on some terminals is not doing its job.
/// </remarks>
internal sealed class HierarchyPrinter(TextWriter writer) : ISolidVisitor
{
    private string _prefix = string.Empty;
    private bool _isLast = true;

    public void PrintScene(Scene scene)
    {
        Camera camera = scene.Camera;
        writer.WriteLine(
            $"Camera   position {Format.Vector(camera.Position)}"
            + $"  lookAt {Format.Vector(camera.LookAt)}"
            + $"  up {Format.Vector(camera.Up)}"
            + $"  fov {Format.Number(camera.FovDegrees)}");

        // Printed even when the file says nothing, so the defaults in force are visible
        // rather than implied.
        writer.WriteLine(
            $"Render   maxBounces {scene.Render.MaxBounces}"
            + $"  exposure {Format.Number(scene.Render.Exposure)}");

        writer.WriteLine();
        writer.WriteLine(scene.Lights.Count == 0 ? "Lights   (none)" : "Lights");

        for (int i = 0; i < scene.Lights.Count; i++)
        {
            string connector = i == scene.Lights.Count - 1 ? "`- " : "+- ";
            writer.WriteLine("  " + connector + Describe(scene.Lights[i]));
        }

        writer.WriteLine();
        writer.WriteLine(scene.Roots.Count == 0 ? "Solids   (none)" : "Solids");

        for (int i = 0; i < scene.Roots.Count; i++)
        {
            Descend(scene.Roots[i], "  ", i == scene.Roots.Count - 1);
        }
    }

    public void VisitSphere(Sphere sphere) => WriteSolid(
        sphere,
        $"center {Format.Vector(sphere.Center)}  radius {Format.Number(sphere.Radius)}");

    public void VisitBox(Box box) => WriteSolid(
        box,
        $"min {Format.Vector(box.Min)}  max {Format.Vector(box.Max)}");

    public void VisitCylinder(Cylinder cylinder) => WriteSolid(
        cylinder,
        $"base {Format.Vector(cylinder.Base)}  cap {Format.Vector(cylinder.Cap)}"
        + $"  radius {Format.Number(cylinder.Radius)}");

    public void VisitCone(Cone cone) => WriteSolid(
        cone,
        $"base {Format.Vector(cone.Base)} r {Format.Number(cone.BaseRadius)}"
        + $"  cap {Format.Vector(cone.Cap)} r {Format.Number(cone.CapRadius)}");

    public void VisitPlane(Plane plane) => WriteSolid(
        plane,
        $"normal {Format.Vector(plane.Normal)}  distance {Format.Number(plane.Distance)}");

    public void VisitTorus(Torus torus) => WriteSolid(
        torus,
        $"center {Format.Vector(torus.Center)}"
        + $"  majorRadius {Format.Number(torus.MajorRadius)}"
        + $"  minorRadius {Format.Number(torus.MinorRadius)}");

    public void VisitPrism(Prism prism) => WriteSolid(
        prism,
        $"bottom {Format.Number(prism.Bottom)}  top {Format.Number(prism.Top)}"
        + $"  {Describe(prism.Points.Count, "point")}");

    public void VisitLathe(Lathe lathe) => WriteSolid(
        lathe,
        Describe(lathe.Points.Count, "point"));

    public void VisitBlob(Blob blob) => WriteSolid(
        blob,
        $"threshold {Format.Number(blob.Threshold)}"
        + $"  {Describe(blob.Components.Count, "component")}");

    public void VisitUnion(Union union) => WriteOperation(union);

    public void VisitIntersection(Intersection intersection) => WriteOperation(intersection);

    public void VisitDifference(Difference difference) => WriteOperation(difference);

    /// <summary>
    /// A count and its noun. The point lists of a prism or a lathe are long enough that
    /// printing them would push the material and the transforms off the line.
    /// </summary>
    private static string Describe(int count, string noun) =>
        $"{count} {noun}{(count == 1 ? "" : "s")}";

    private void WriteOperation(CsgOperation operation)
    {
        WriteSolid(operation, string.Empty);

        // A last child's subtree hangs under blank space; a middle child's needs the
        // vertical bar so the branches below it stay connected to their parent.
        string childPrefix = _prefix + (_isLast ? "   " : "|  ");

        for (int i = 0; i < operation.Operands.Count; i++)
        {
            Descend(operation.Operands[i], childPrefix, i == operation.Operands.Count - 1);
        }
    }

    private void Descend(Solid solid, string prefix, bool isLast)
    {
        string savedPrefix = _prefix;
        bool savedIsLast = _isLast;

        _prefix = prefix;
        _isLast = isLast;
        solid.Accept(this);

        _prefix = savedPrefix;
        _isLast = savedIsLast;
    }

    private void WriteSolid(Solid solid, string details)
    {
        StringBuilder line = new();
        line.Append(_prefix).Append(_isLast ? "`- " : "+- ").Append(solid.Kind);

        if (details.Length > 0)
        {
            line.Append("  ").Append(details);
        }

        if (solid.Material is { } material)
        {
            line.Append("  material=").Append(Describe(material));
        }

        foreach (TransformStep step in solid.Transform.Steps)
        {
            line.Append("  ")
                .Append(step.Kind.ToString().ToLowerInvariant())
                .Append(' ')
                .Append(Format.Vector(step.Value));
        }

        writer.WriteLine(line.ToString());
    }

    /// <summary>
    /// A named material prints as its name; an anonymous one prints its components.
    /// </summary>
    /// <remarks>
    /// Roughness and metallic are always shown because they have non-obvious defaults;
    /// emission and the transmissive fields only when they are in play — printing
    /// <c>emission &lt;0, 0, 0&gt;</c> and <c>ior 1.5</c> on every ordinary surface would
    /// bury the one line where they matter.
    /// </remarks>
    private static string Describe(Material material)
    {
        if (material.Name is { } name)
        {
            return name;
        }

        StringBuilder described = new();
        described
            .Append("color ").Append(Format.Vector(material.Color))
            .Append(" roughness ").Append(Format.Number(material.Roughness))
            .Append(" metallic ").Append(Format.Number(material.Metallic));

        if (material.Emission != Vector3.Zero)
        {
            described.Append(" emission ").Append(Format.Vector(material.Emission));
        }

        // `ior` rides along with `transmission` rather than standing on its own: it also
        // sets a dielectric's reflectance, so it is never inert, but on an opaque surface
        // the default is the only value anyone has ever wanted.
        if (material.Transmission > 0f)
        {
            described
                .Append(" transmission ").Append(Format.Number(material.Transmission))
                .Append(" ior ").Append(Format.Number(material.Ior));

            if (material.Absorption != Vector3.Zero)
            {
                described.Append(" absorption ").Append(Format.Vector(material.Absorption));
            }
        }

        return described.ToString();
    }

    private static string Describe(Light light) => light switch
    {
        PointLight point =>
            $"PointLight        position {Format.Vector(point.Position)}"
            + $"  color {Format.Vector(point.Color)}"
            + $"  intensity {Format.Number(point.Intensity)}"
            + $"  radius {Format.Number(point.Radius)}",

        DirectionalLight directional =>
            $"DirectionalLight  direction {Format.Vector(directional.Direction)}"
            + $"  color {Format.Vector(directional.Color)}"
            + $"  intensity {Format.Number(directional.Intensity)}",

        _ => light.Kind,
    };
}
