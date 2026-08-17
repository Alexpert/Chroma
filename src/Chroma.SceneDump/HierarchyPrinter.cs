using System.Numerics;
using System.Text;
using Chroma.Core.Model;
using Chroma.Core.Model.Geometry;
using Chroma.Core.Model.Geometry.Operations;
using Chroma.Core.Model.Geometry.Primitives;
using Chroma.Core.Model.Lighting;
using Chroma.Core.Model.Materials;

// System.Numerics has a Plane of its own — a mathematical plane, not a solid — and this file
// needs its vectors. The alias says which one is meant once, rather than at each mention.
using Plane = Chroma.Core.Model.Geometry.Primitives.Plane;

namespace Chroma.SceneDump;

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
            + $"  exposure {Format.Number(scene.Render.Exposure)}"
            + $"  seed {scene.Render.Seed}"

            // How the file wrote its angles, not how the model holds them -- everything below
            // is degrees either way. It is here because "was the file read the way I meant" is
            // what this tool is for, and a scene in radians read as degrees is exactly that.
            + $"  angles {(scene.Render.AnglesInRadians ? "radians" : "degrees")}");

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
        + $"  {Describe(prism.Points.Count, "point")}{Split(prism.ContourSizes)}");

    public void VisitLathe(Lathe lathe) => WriteSolid(
        lathe,
        Describe(lathe.Points.Count, "point") + Split(lathe.ContourSizes));

    public void VisitBlob(Blob blob) => WriteSolid(
        blob,
        $"threshold {Format.Number(blob.Threshold)}"
        + $"  {Describe(blob.Components.Count, "component")}");

    public void VisitSphereSweep(SphereSweep sweep) => WriteSolid(
        sweep,
        Describe(sweep.Spheres.Count, "sphere"));

    public void VisitQuadric(Quadric quadric) => WriteSolid(
        quadric,
        $"squared {Format.Vector(quadric.Squared)}"
        + $"  mixed {Format.Vector(quadric.Mixed)}"
        + $"  linear {Format.Vector(quadric.Linear)}"
        + $"  constant {Format.Number(quadric.Constant)}");

    // The path rather than the vertex count first: a mesh is identified by the file it came
    // from, and a dump of a scene holding three of them is read to find out which is which.
    public void VisitMesh(Mesh mesh) => WriteSolid(
        mesh,
        $"{Path.GetFileName(mesh.Path)}"
        + $"  {Describe(mesh.TriangleCount, "triangle")}"
        + $"  {mesh.Positions.Count} vertices"
        + $"  maxSpans {mesh.MaxSpans}"
        + (mesh.Normals is null ? string.Empty : "  smooth"));

    public void VisitUnion(Union union) => WriteOperation(union);

    public void VisitIntersection(Intersection intersection) => WriteOperation(intersection);

    public void VisitDifference(Difference difference) => WriteOperation(difference);

    /// <summary>
    /// A count and its noun. The point lists of a prism or a lathe are long enough that
    /// printing them would push the material and the transforms off the line.
    /// </summary>
    private static string Describe(int count, string noun) =>
        $"{count} {noun}{(count == 1 ? "" : "s")}";

    /// <summary>
    /// How a point list divides into contours, and nothing at all when it does not divide.
    /// The usual solid has one, so saying so on every line would be noise.
    /// </summary>
    private static string Split(IReadOnlyList<int> contourSizes) =>
        contourSizes.Count > 1 ? $" in {Describe(contourSizes.Count, "contour")}" : string.Empty;

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

            // `anisotropy` rides along with `scattering` for the same reason `ior` rides with
            // `transmission`: without a medium to scatter in, it describes nothing.
            if (material.Scattering > 0f)
            {
                described.Append(" scattering ").Append(Format.Number(material.Scattering));

                if (material.Anisotropy != 0f)
                {
                    described.Append(" anisotropy ").Append(Format.Number(material.Anisotropy));
                }
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
