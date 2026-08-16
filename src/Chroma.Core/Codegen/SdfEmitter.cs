using System.Numerics;
using Chroma.Core.Compilation;
using Chroma.Core.Model.Geometry;
using Chroma.Core.Model.Geometry.Operations;
using Chroma.Core.Model.Geometry.Primitives;
using Chroma.Core.Model.Materials;
using Chroma.Core.Sdl.Source;

using Plane = Chroma.Core.Model.Geometry.Primitives.Plane;

namespace Chroma.Core.Codegen;

/// <summary>
/// The distance-field backend: walks the same solid tree <see cref="GeometryEmitter"/> walks and
/// writes a signed distance function plus a sphere tracer, instead of span lists and boolean
/// operators over them.
/// </summary>
/// <remarks>
/// <para>
/// This exists to answer a question rather than to render production images. Iteration 0 chose
/// exact intervals over distance fields on reasoning alone and nothing tested it; both backends
/// fill the same <see cref="CompiledScene.Geometry"/> slot, are spliced at the same marker, and
/// answer the same <c>traceScene</c>, so everything above the seam is shared and the two differ
/// in exactly one thing. See documents/raymarching.md.
/// </para>
/// <para>
/// <b>Two decisions shape all of it.</b> The march loop is bounded by a <b>uniform</b>, never by
/// a constant: documents/gpu-backends.md records that the driver unrolls every loop with a
/// compile-time bound, and a hundred unrolled copies of a whole scene's <c>map</c> is a program
/// no driver will take. And normals stay <b>analytic</b>: because <c>map</c> returns which leaf
/// was nearest, <c>hitNormal</c> works unchanged, where a raymarcher without that index has to
/// spend four more <c>map</c> evaluations on a tetrahedron gradient.
/// </para>
/// <para>
/// <b>The leaf tables are duplicated from <see cref="GeometryEmitter"/> on purpose.</b> They
/// describe leaves rather than the tree, so both backends need the identical bytes, and the
/// shading half reads them either way. Sharing them means editing the span emitter, whose output
/// this work must leave byte-identical to stay honest. If this backend outlives the experiment,
/// that is the first thing to extract.
/// </para>
/// </remarks>
internal sealed class SdfEmitter : ISolidVisitor<SdfEmitter.Node>
{
    /// <summary>One subtree's result inside the generated <c>map</c>: the locals holding its
    /// distance and its leaf code.</summary>
    /// <remarks>
    /// No bounding box travels with it, unlike <see cref="GeometryEmitter.Node"/>. The paper's
    /// §3.3 bounding-volume optimisation is not built here, so the field evaluates every leaf at
    /// every march step, and carrying a box nothing reads would only suggest otherwise. Which box
    /// belongs to which operator is still recorded, in the span emitter, which uses it.
    /// </remarks>
    internal readonly record struct Node(string Distance, string Leaf);

    private readonly GlslWriter _leaves = new();
    private readonly List<float> _primitives = [];
    private readonly List<float> _materials = [];
    private readonly List<float> _shapeData = [];
    private readonly Dictionary<Material, int> _materialIndices = [];

    /// <summary>The roots, as the locals inside <c>map</c> that hold each one's answer.</summary>
    private readonly List<Node> _roots = [];

    private GlslWriter _body = new();
    private Matrix4x4 _ancestorTransform = Matrix4x4.Identity;
    private Material? _inheritedMaterial;
    private bool _failed;
    private int _temporaries;
    private int _shapeOffset;

    /// <summary>
    /// True when this leaf sits under an odd number of subtractions.
    /// </summary>
    /// <remarks>
    /// The span path carries this in the sign of a span's surface reference, decided at run time.
    /// Here it is a static property of the path from the root, so it is baked into the leaf code
    /// the map returns. Getting it wrong renders the inside of every drilled hole black, which
    /// documents/csg-raytracing.md names as the most commonly botched detail in a CSG renderer.
    /// </remarks>
    private bool _negated;

    private readonly DiagnosticBag _diagnostics;

    public SdfEmitter(DiagnosticBag diagnostics) => _diagnostics = diagnostics;

    public IReadOnlyList<float> Primitives => _primitives;

    public IReadOnlyList<float> Materials => _materials;

    public IReadOnlyList<float> Shapes => _shapeData;

    public int LeafCount => _primitives.Count / GpuLayout.PrimitiveStride;

    /// <summary>Walks one top-level solid, appending its statements to the shared map body.</summary>
    /// <remarks>
    /// Roots are folded into one field with <c>min</c>, which is a union. The span backend
    /// resolves them one at a time instead, and documents/scene-language.md records the one case
    /// where that differs: two overlapping transmissive solids written at the top level show
    /// their interior faces there and do not here. Invisible for opaque solids, and worth knowing
    /// before it is mistaken for a bug in the marcher.
    /// </remarks>
    public void EmitRoot(Solid solid)
    {
        Node node = Descend(solid);

        if (_failed)
        {
            return;
        }

        _roots.Add(node);
    }

    public string Build()
    {
        var w = new GlslWriter();

        w.Line("// ===== generated geometry: distance fields =========================================");
        w.Line("// Emitted for this scene and no other, by Chroma.Core.Codegen.SdfEmitter. This is the");
        w.Line("// --sdf backend: the same tree as the span path, folded into one signed distance");
        w.Line("// function and sphere-traced. See documents/raymarching.md.");
        w.Line();

        WritePrimitiveLibrary(w);
        Paste(w, _leaves);
        WriteMap(w);
        WriteTraceScene(w);

        return w.ToString();
    }

    // --- The hand-written half, emitted once per scene -------------------------------------

    /// <summary>
    /// The canonical distance functions, one per kind, and the box distance the bounding-volume
    /// guard needs.
    /// </summary>
    /// <remarks>
    /// Written here rather than in raytrace.glsl because a scene that uses none of them should
    /// emit none of them. The span path's equivalents live in the hand-written shader because
    /// they were there before code generation existed; there is no reason to repeat that.
    /// </remarks>
    private static void WritePrimitiveLibrary(GlslWriter w)
    {
        w.Line("// --- Canonical distance functions -------------------------------------------------");
        w.Line("// Every primitive is evaluated in the same canonical space the span backend uses, so");
        w.Line("// the two backends are looking at the same solid: unit sphere, [-1,1] box, unit column");
        w.Line("// along +Y. The transform is the leaf's own baked inverse matrix.");
        w.Line();

        w.Line("// How far short of its own estimate a blob steps. The estimate is not a bound, so this");
        w.Line("// buys a smaller chance of stepping through the surface and no guarantee at all.");
        w.Line("const float BLOB_UNDERSTEP = 0.4;");
        w.Line();

        w.Line("float sdSphereUnit(vec3 p) { return length(p) - 1.0; }");
        w.Line();
        w.Line("float sdPlaneUnit(vec3 p) { return p.y; }");
        w.Line();

        w.Line("float sdBoxUnit(vec3 p)");
        w.Line("{");
        w.Line("    vec3 q = abs(p) - vec3(1.0);");
        w.Line("    return length(max(q, 0.0)) + min(max(q.x, max(q.y, q.z)), 0.0);");
        w.Line("}");
        w.Line();

        w.Line("float sdTorusUnit(vec3 p, float minor)");
        w.Line("{");
        w.Line("    return length(vec2(length(p.xz) - 1.0, p.y)) - minor;");
        w.Line("}");
        w.Line();

        w.Line("// A capped cone from radius 1 at y = 0 to radius `cap` at y = 1. cap == 1 is the");
        w.Line("// cylinder exactly, so one function serves both and no cylinder case is written.");
        w.Line("float sdConeUnit(vec3 p, float cap)");
        w.Line("{");
        w.Line("    vec2  q  = vec2(length(p.xz), p.y - 0.5);");
        w.Line("    float h  = 0.5;");
        w.Line("    vec2  k1 = vec2(cap, h);");
        w.Line("    vec2  k2 = vec2(cap - 1.0, 2.0 * h);");
        w.Line("    vec2  ca = vec2(q.x - min(q.x, q.y < 0.0 ? 1.0 : cap), abs(q.y) - h);");
        w.Line("    vec2  cb = q - k1 + k2 * clamp(dot(k1 - q, k2) / dot(k2, k2), 0.0, 1.0);");
        w.Line("    float s  = (cb.x < 0.0 && ca.y < 0.0) ? -1.0 : 1.0;");
        w.Line("    return s * sqrt(min(dot(ca, ca), dot(cb, cb)));");
        w.Line("}");
        w.Line();

        w.Line("// The exact distance to a round cone, which is the convex hull of two spheres. A");
        w.Line("// sphereSweep is the union of these, and a union of exact distances is exact.");
        w.Line("float sdRoundCone(vec3 p, vec3 a, vec3 b, float r1, float r2)");
        w.Line("{");
        w.Line("    vec3  ba = b - a;");
        w.Line("    float l2 = dot(ba, ba);");
        w.Line("    if (l2 < TINY) return length(p - a) - max(r1, r2);");
        w.Line();
        w.Line("    float rr  = r1 - r2;");
        w.Line("    float a2  = l2 - rr * rr;");
        w.Line("    float il2 = 1.0 / l2;");
        w.Line();
        w.Line("    vec3  pa = p - a;");
        w.Line("    float y  = dot(pa, ba);");
        w.Line("    float z  = y - l2;");
        w.Line("    vec3   d = pa * l2 - ba * y;");
        w.Line("    float x2 = dot(d, d);");
        w.Line("    float y2 = y * y * l2;");
        w.Line("    float z2 = z * z * l2;");
        w.Line();
        w.Line("    float k = sign(rr) * rr * rr * x2;");
        w.Line("    if (sign(z) * a2 * z2 > k) return sqrt(x2 + z2) * il2 - r2;");
        w.Line("    if (sign(y) * a2 * y2 < k) return sqrt(x2 + y2) * il2 - r1;");
        w.Line("    return (sqrt(x2 * a2 * il2) + y * rr) * il2 - r1;");
        w.Line("}");
        w.Line();
    }

    // --- Leaves -----------------------------------------------------------------------------

    public Node VisitSphere(Sphere sphere) => EmitLeaf(
        sphere,
        PrimitiveKind.Sphere,
        Matrix4x4.CreateScale(sphere.Radius)
        * Matrix4x4.CreateTranslation(sphere.Center)
        * _ancestorTransform,
        "sdSphereUnit(q)");

    public Node VisitBox(Box box) => EmitLeaf(
        box,
        PrimitiveKind.Box,
        Matrix4x4.CreateScale((box.Max - box.Min) * 0.5f)
        * Matrix4x4.CreateTranslation((box.Max + box.Min) * 0.5f)
        * _ancestorTransform,
        "sdBoxUnit(q)");

    public Node VisitCylinder(Cylinder cylinder)
    {
        Matrix4x4 basis = AxisBasis(cylinder.Base, cylinder.Cap, out float height);

        return EmitLeaf(
            cylinder,
            PrimitiveKind.Cylinder,
            Matrix4x4.CreateScale(cylinder.Radius, height, cylinder.Radius)
            * basis
            * Matrix4x4.CreateTranslation(cylinder.Base)
            * _ancestorTransform,
            // A cylinder is the cone whose taper is 1. One distance function, not two.
            "sdConeUnit(q, 1.0)");
    }

    public Node VisitCone(Cone cone)
    {
        Vector3 basePoint = cone.Base;
        Vector3 capPoint = cone.Cap;
        float baseRadius = cone.BaseRadius;
        float capRadius = cone.CapRadius;

        if (capRadius > baseRadius)
        {
            (basePoint, capPoint) = (capPoint, basePoint);
            (baseRadius, capRadius) = (capRadius, baseRadius);
        }

        Matrix4x4 basis = AxisBasis(basePoint, capPoint, out float height);
        float taper = capRadius / baseRadius;

        return EmitLeaf(
            cone,
            PrimitiveKind.Cone,
            Matrix4x4.CreateScale(baseRadius, height, baseRadius)
            * basis
            * Matrix4x4.CreateTranslation(basePoint)
            * _ancestorTransform,
            $"sdConeUnit(q, {GlslWriter.Float(taper)})",
            paramA: taper);
    }

    public Node VisitPlane(Plane plane)
    {
        Vector3 normal = Vector3.Normalize(plane.Normal);

        return EmitLeaf(
            plane,
            PrimitiveKind.Plane,
            UpBasis(normal)
            * Matrix4x4.CreateTranslation(normal * plane.Distance)
            * _ancestorTransform,
            "sdPlaneUnit(q)");
    }

    public Node VisitTorus(Torus torus)
    {
        float minor = torus.MinorRadius / torus.MajorRadius;

        return EmitLeaf(
            torus,
            PrimitiveKind.Torus,
            Matrix4x4.CreateScale(torus.MajorRadius)
            * Matrix4x4.CreateTranslation(torus.Center)
            * _ancestorTransform,
            $"sdTorusUnit(q, {GlslWriter.Float(minor)})",
            paramA: minor);
    }

    /// <summary>
    /// A closed contour in XZ swept from <c>y = 0</c> to <c>y = 1</c>. The distance is the 2D
    /// polygon distance combined with the slab, which is exact.
    /// </summary>
    public Node VisitPrism(Prism prism)
    {
        AppendEdges(prism.Points, prism.ContourSizes, prism.Smooth);
        float height = prism.Top - prism.Bottom;

        return EmitLeaf(
            prism,
            PrimitiveKind.Prism,
            Matrix4x4.CreateScale(1f, height, 1f)
            * Matrix4x4.CreateTranslation(0f, prism.Bottom, 0f)
            * _ancestorTransform,
            body: null,
            paramA: _shapeOffset,
            paramB: prism.Points.Count,
            points: prism.Points,
            contours: prism.ContourSizes);
    }

    /// <summary>
    /// A closed outline in the <c>(radius, y)</c> half-plane, revolved about Y. The distance from
    /// a point to the revolved surface <b>is</b> the half-plane distance from that point's
    /// <c>(radius, y)</c> to the outline: rotating any candidate into the point's own half-plane
    /// cannot lengthen it. So this is exact, and a Bézier outline is exact on the polyline it was
    /// flattened to, which is the same polyline the span backend sees.
    /// </summary>
    public Node VisitLathe(Lathe lathe)
    {
        AppendEdges(lathe.Points, lathe.ContourSizes, lathe.Smooth);

        return EmitLeaf(
            lathe,
            PrimitiveKind.Lathe,
            _ancestorTransform,
            body: null,
            paramA: _shapeOffset,
            paramB: lathe.Points.Count,
            points: lathe.Points,
            contours: lathe.ContourSizes);
    }

    /// <summary>
    /// The one primitive with no distance function at all, and the reason this experiment is
    /// worth running.
    /// </summary>
    /// <remarks>
    /// A blob is an isosurface of a field, not a distance field: the field says how strongly the
    /// components reach a point, not how far the surface is. The first-order estimate
    /// <c>f / |grad f|</c> below is <b>not a lower bound</b>. Where the field curves sharply it
    /// overshoots, the march steps through the surface, and the blob renders with holes. The
    /// understep coefficient makes that rarer and does not make it impossible.
    /// <para>
    /// This is the exact mirror of the span backend, where the blob is one of the <i>easiest</i>
    /// primitives because a sum of quartics is still a quartic and Ferrari solves it. Same shape,
    /// opposite difficulty, which is the clearest thing this backend has to say.
    /// </para>
    /// </remarks>
    public Node VisitBlob(Blob blob)
    {
        _shapeOffset = _shapeData.Count / GpuLayout.ShapeStride;

        _shapeData.Add(blob.Threshold);
        _shapeData.Add(0f);
        _shapeData.Add(0f);
        _shapeData.Add(0f);

        var balls = new List<Vector4>(blob.Components.Count);
        var caps = new List<Vector4>(blob.Components.Count);

        foreach (BlobComponent component in blob.Components)
        {
            _shapeData.Add(component.Base.X);
            _shapeData.Add(component.Base.Y);
            _shapeData.Add(component.Base.Z);
            _shapeData.Add(component.Radius);

            _shapeData.Add(component.Cap.X);
            _shapeData.Add(component.Cap.Y);
            _shapeData.Add(component.Cap.Z);
            _shapeData.Add(component.Strength);

            balls.Add(new Vector4(component.Base, component.Radius));
            caps.Add(new Vector4(component.Cap, component.Strength));
        }

        return EmitLeaf(
            blob,
            PrimitiveKind.Blob,
            _ancestorTransform,
            body: null,
            paramA: _shapeOffset,
            paramB: blob.Components.Count,
            balls: balls,
            caps: caps,
            threshold: blob.Threshold);
    }

    /// <summary>
    /// Refused, and this is the one primitive that has no answer here rather than a poor one.
    /// </summary>
    /// <remarks>
    /// The blob's <c>f / |grad f|</c> is not a distance either, and it is kept because a blob
    /// is bounded, so an overshoot lands outside a box some other test catches. A quadric is
    /// neither: its first-order estimate overshoots harder, and it is unbounded, so there is no
    /// outer surface for a march to fail against and no place a wrong step is recovered. A
    /// scene that renders as noise with no diagnostic is worse than one that will not render.
    /// </remarks>
    public Node VisitQuadric(Quadric quadric)
    {
        _failed = true;
        _diagnostics.Error(
            quadric.Origin,
            "'quadric' has no distance bound and is not supported by --sdf; "
            + "trace it with the default backend");

        return new Node("0.0", "0");
    }

    public Node VisitSphereSweep(SphereSweep sweep)
    {
        _shapeOffset = _shapeData.Count / GpuLayout.ShapeStride;

        foreach (Vector4 sphere in sweep.Spheres)
        {
            _shapeData.Add(sphere.X);
            _shapeData.Add(sphere.Y);
            _shapeData.Add(sphere.Z);
            _shapeData.Add(sphere.W);
        }

        return EmitLeaf(
            sweep,
            PrimitiveKind.SphereSweep,
            _ancestorTransform,
            body: null,
            paramA: _shapeOffset,
            paramB: sweep.Spheres.Count,
            balls: sweep.Spheres);
    }

    // --- Operators --------------------------------------------------------------------------

    public Node VisitUnion(Union union) => EmitOperation(union, "Union");

    public Node VisitIntersection(Intersection intersection) => EmitOperation(intersection, "Intersection");

    public Node VisitDifference(Difference difference) => EmitOperation(difference, "Difference");

    /// <summary>
    /// One operator, as two statements per operand: the combined distance, and which leaf that
    /// distance came from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>min</c> for union, <c>max</c> for intersection, <c>max(a, -b)</c> for difference. Only
    /// the first is exact. The other two are <b>lower bounds</b> rather than distances, which
    /// sphere tracing tolerates by construction: the solid they describe is right, the field is
    /// not, and the price is march steps near concave seams rather than wrong geometry. That is
    /// the honest form of iteration 0's objection, and it is smaller than iteration 0 thought.
    /// </para>
    /// <para>
    /// The leaf code travels beside the distance so the hit can name a primitive, which is what
    /// lets the shading half keep its exact analytic normal. Negating the code where a subtrahend
    /// wins is what flips that normal into the cavity.
    /// </para>
    /// </remarks>
    private Node EmitOperation(CsgOperation operation, string name)
    {
        IReadOnlyList<Solid> operands = operation.Operands;

        Node accumulated = Descend(operands[0]);

        for (int i = 1; i < operands.Count; i++)
        {
            // Everything under a difference except its first operand is subtracted, so its
            // leaves' normals point into the result and have to be flipped.
            bool saved = _negated;
            if (name == "Difference")
            {
                _negated = !_negated;
            }

            Node right = Descend(operands[i]);
            _negated = saved;

            string d = Fresh("d");
            string l = Fresh("l");

            switch (name)
            {
                case "Union":
                    _body.Line($"float {d} = min({accumulated.Distance}, {right.Distance});");
                    _body.Line(
                        $"int   {l} = {accumulated.Distance} <= {right.Distance} "
                        + $"? {accumulated.Leaf} : {right.Leaf};");
                    break;

                case "Intersection":
                    _body.Line($"float {d} = max({accumulated.Distance}, {right.Distance});");
                    _body.Line(
                        $"int   {l} = {accumulated.Distance} >= {right.Distance} "
                        + $"? {accumulated.Leaf} : {right.Leaf};");
                    break;

                default:
                    _body.Line($"float {d} = max({accumulated.Distance}, -{right.Distance});");
                    _body.Line(
                        $"int   {l} = {accumulated.Distance} >= -{right.Distance} "
                        + $"? {accumulated.Leaf} : {right.Leaf};");
                    break;
            }

            _body.Line();
            accumulated = new Node(d, l);
        }

        return accumulated;
    }

    private Node Descend(Solid solid)
    {
        Matrix4x4 savedTransform = _ancestorTransform;
        Material? savedMaterial = _inheritedMaterial;

        _ancestorTransform = solid.Transform.Matrix * _ancestorTransform;
        _inheritedMaterial = solid.Material ?? _inheritedMaterial;

        Node node = solid.Accept(this);

        _ancestorTransform = savedTransform;
        _inheritedMaterial = savedMaterial;
        return node;
    }

    // --- Emitting one leaf ------------------------------------------------------------------

    /// <param name="body">
    /// The canonical distance expression over the local point <c>q</c>, for the six primitives
    /// that are a formula. Null for the four defined by a list, whose bodies are written from
    /// their points instead.
    /// </param>
    private Node EmitLeaf(
        Solid solid,
        PrimitiveKind kind,
        Matrix4x4 toWorld,
        string? body,
        float paramA = 0f,
        float paramB = 0f,
        IReadOnlyList<Vector2>? points = null,
        IReadOnlyList<int>? contours = null,
        IReadOnlyList<Vector4>? balls = null,
        IReadOnlyList<Vector4>? caps = null,
        float threshold = 0f)
    {
        if (!Matrix4x4.Invert(toWorld, out Matrix4x4 toLocal))
        {
            _failed = true;
            _diagnostics.Error(
                solid.Origin,
                $"'{solid.Kind.ToLowerInvariant()}' has a transform that cannot be inverted; "
                + "a zero scale collapses the solid to nothing");

            return new Node("0.0", "0");
        }

        int index = LeafCount;

        bool listShaped = kind is PrimitiveKind.Prism or PrimitiveKind.Lathe
            or PrimitiveKind.Blob or PrimitiveKind.SphereSweep;

        _primitives.Add((float)kind);
        _primitives.Add(InternMaterial(_inheritedMaterial ?? Material.Default));
        _primitives.Add(listShaped ? _shapeOffset : paramA);
        _primitives.Add(paramB);
        AppendRows(toLocal);

        WriteLeafFunction(
            index, solid, kind, toLocal, body, points, contours, balls, caps, threshold);

        string d = Fresh("d");
        string l = Fresh("l");

        _body.Line($"float {d} = leaf{index}(p);");
        _body.Line($"int   {l} = {(_negated ? -(index + 1) : index + 1)};");
        _body.Line();

        return new Node(d, l);
    }

    /// <summary>
    /// Writes <c>float leafN(vec3 p)</c>: into the primitive's own space, evaluate, and scale the
    /// answer back to world units.
    /// </summary>
    /// <remarks>
    /// <b>The scale factor is the part that is easy to get wrong and fatal to omit.</b> A distance
    /// measured in a space the matrix has stretched is not a distance in the world, and sphere
    /// tracing needs a value that never overestimates. If <c>A</c> is the local-to-world linear
    /// part then every world separation is at least <c>sigmaMin(A)</c> times the local one, and
    /// <c>sigmaMin(A)</c> is <c>1 / sigmaMax(M)</c> for the inverse <c>M</c> that is baked here.
    /// Skip it and a scaled-up solid oversteps and renders full of holes; use a loose bound like
    /// the Frobenius norm and every scene pays 1.7x the march steps for nothing.
    /// </remarks>
    private void WriteLeafFunction(
        int index,
        Solid solid,
        PrimitiveKind kind,
        Matrix4x4 toLocal,
        string? body,
        IReadOnlyList<Vector2>? points,
        IReadOnlyList<int>? contours,
        IReadOnlyList<Vector4>? balls,
        IReadOnlyList<Vector4>? caps,
        float threshold)
    {
        float lipschitz = 1f / LargestSingularValue(toLocal);

        _leaves.Line($"// {solid.Kind.ToLowerInvariant()}, leaf {index}");
        _leaves.Open($"float leaf{index}(vec3 p)");
        _leaves.Line($"const mat4 M = {GlslWriter.Mat4(toLocal)};");
        _leaves.Line("vec3 q = (M * vec4(p, 1.0)).xyz;");
        _leaves.Line();

        string scale = GlslWriter.Float(lipschitz);

        if (body is not null)
        {
            _leaves.Line($"return {body} * {scale};");
        }
        else
        {
            switch (kind)
            {
                case PrimitiveKind.Prism:
                    WritePrismBody(points!, contours!, scale);
                    break;
                case PrimitiveKind.Lathe:
                    WriteLatheBody(points!, contours!, scale);
                    break;
                case PrimitiveKind.Blob:
                    WriteBlobBody(balls!, caps!, threshold, scale);
                    break;
                default:
                    WriteSweepBody(balls!, scale);
                    break;
            }
        }

        _leaves.Close();
        _leaves.Line();
    }

    /// <summary>The contours as a const array of edges, the same shape LeafEmitter uses.</summary>
    private void WriteEdges(IReadOnlyList<Vector2> points, IReadOnlyList<int> contours)
    {
        int n = points.Count;
        _leaves.Line($"const vec4 edges[{n}] = vec4[{n}](");

        int start = 0;

        foreach (int size in contours)
        {
            for (int i = 0; i < size; i++)
            {
                Vector2 a = points[start + i];
                Vector2 b = points[start + ((i + 1) % size)];
                string comma = start + i == n - 1 ? "" : ",";
                _leaves.Line($"    {GlslWriter.Vec4(a.X, a.Y, b.X, b.Y)}{comma}");
            }

            start += size;
        }

        _leaves.Line(");");
        _leaves.Line();
    }

    /// <summary>
    /// The signed distance from a point to closed polygons: nearest edge for the magnitude, a
    /// crossing count for the sign. Written inline per leaf rather than shared, because the
    /// contour is a const array of this leaf's own size.
    /// </summary>
    /// <remarks>
    /// Several contours need nothing here beyond edges that close per contour. The nearest edge
    /// over all of them is the nearest edge, and the crossing count over all of them is the
    /// even-odd rule, which is the same rule that makes a contour inside another a hole in the
    /// span backend.
    /// </remarks>
    private void WritePolygonDistance(
        IReadOnlyList<Vector2> points,
        IReadOnlyList<int> contours,
        string point)
    {
        WriteEdges(points, contours);

        _leaves.Line($"vec2  w0 = {point} - edges[0].xy;");
        _leaves.Line("float best = dot(w0, w0);");
        _leaves.Line("float side = 1.0;");
        _leaves.Line();
        _leaves.Open($"for (int e = 0; e < {points.Count}; ++e)");
        _leaves.Line("vec2 a = edges[e].xy;");
        _leaves.Line("vec2 b = edges[e].zw;");
        _leaves.Line("vec2 ab = b - a;");
        _leaves.Line($"vec2 ap = {point} - a;");
        _leaves.Line();
        _leaves.Line("vec2 off = ap - ab * clamp(dot(ap, ab) / max(dot(ab, ab), TINY), 0.0, 1.0);");
        _leaves.Line("best = min(best, dot(off, off));");
        _leaves.Line();
        _leaves.Line("// Crossing count, with the range half-open exactly as the span path's is, so a");
        _leaves.Line("// point level with a shared vertex is counted once and the sign does not flip twice.");
        _leaves.Line($"bvec3 c = bvec3({point}.y >= a.y, {point}.y < b.y, ab.x * ap.y > ab.y * ap.x);");
        _leaves.Line("if (all(c) || all(not(c))) side = -side;");
        _leaves.Close();
        _leaves.Line();
    }

    private void WritePrismBody(
        IReadOnlyList<Vector2> points,
        IReadOnlyList<int> contours,
        string scale)
    {
        _leaves.Line("vec2 xz = q.xz;");
        _leaves.Line();
        WritePolygonDistance(points, contours, "xz");

        _leaves.Line("float plane_ = side * sqrt(best);");
        _leaves.Line("float slab   = max(-q.y, q.y - 1.0);");
        _leaves.Line();
        _leaves.Line("// The prism is the contour extruded and capped, so it is the 2D distance and the");
        _leaves.Line("// slab combined the way any two orthogonal extents combine.");
        _leaves.Line("vec2 outside = max(vec2(plane_, slab), 0.0);");
        _leaves.Line($"return (min(max(plane_, slab), 0.0) + length(outside)) * {scale};");
    }

    private void WriteLatheBody(
        IReadOnlyList<Vector2> points,
        IReadOnlyList<int> contours,
        string scale)
    {
        _leaves.Line("// Into the (radius, y) half-plane. The distance to the revolved surface is the");
        _leaves.Line("// distance to the outline here: rotating a candidate into this half-plane can only");
        _leaves.Line("// shorten it, so nothing nearer exists off it.");
        _leaves.Line("vec2 rz = vec2(length(q.xz), q.y);");
        _leaves.Line();
        WritePolygonDistance(points, contours, "rz");

        _leaves.Line($"return side * sqrt(best) * {scale};");
    }

    private void WriteSweepBody(IReadOnlyList<Vector4> balls, string scale)
    {
        int n = balls.Count;

        _leaves.Line($"const vec4 balls[{n}] = vec4[{n}](");
        for (int i = 0; i < n; i++)
        {
            Vector4 ball = balls[i];
            string comma = i == n - 1 ? "" : ",";
            _leaves.Line($"    {GlslWriter.Vec4(ball.X, ball.Y, ball.Z, ball.W)}{comma}");
        }

        _leaves.Line(");");
        _leaves.Line();
        _leaves.Line("float best = INF;");
        _leaves.Line();
        _leaves.Open($"for (int i = 0; i + 1 < {n}; ++i)");
        _leaves.Line("best = min(best, sdRoundCone(q, balls[i].xyz, balls[i + 1].xyz,");
        _leaves.Line("                             balls[i].w, balls[i + 1].w));");
        _leaves.Close();
        _leaves.Line();
        _leaves.Line($"return best * {scale};");
    }

    private void WriteBlobBody(
        IReadOnlyList<Vector4> balls,
        IReadOnlyList<Vector4> caps,
        float threshold,
        string scale)
    {
        int n = balls.Count;

        _leaves.Line($"const vec4 balls[{n}] = vec4[{n}](");
        for (int i = 0; i < n; i++)
        {
            Vector4 ball = balls[i];
            string comma = i == n - 1 ? "" : ",";
            _leaves.Line($"    {GlslWriter.Vec4(ball.X, ball.Y, ball.Z, ball.W)}{comma}");
        }

        _leaves.Line(");");
        _leaves.Line($"const vec4 caps[{n}] = vec4[{n}](");
        for (int i = 0; i < n; i++)
        {
            Vector4 cap = caps[i];
            string comma = i == n - 1 ? "" : ",";
            _leaves.Line($"    {GlslWriter.Vec4(cap.X, cap.Y, cap.Z, cap.W)}{comma}");
        }

        _leaves.Line(");");
        _leaves.Line();
        _leaves.Line("// The field and its gradient in one pass. A component is exactly zero at and beyond");
        _leaves.Line("// its own radius, which is what makes a blob local and the loop cheap.");
        _leaves.Line("float field = 0.0;");
        _leaves.Line("vec3  grad  = vec3(0.0);");
        _leaves.Line();
        _leaves.Open($"for (int i = 0; i < {n}; ++i)");
        _leaves.Line("// Every component is a capsule and a spherical one has both ends at one point, so");
        _leaves.Line("// clamping the foot onto a segment of no length gives that point back.");
        _leaves.Line("vec3  ax = caps[i].xyz - balls[i].xyz;");
        _leaves.Line("float L2 = dot(ax, ax);");
        _leaves.Line("float s  = L2 < TINY ? 0.0 : clamp(dot(q - balls[i].xyz, ax) / L2, 0.0, 1.0);");
        _leaves.Line();
        _leaves.Line("vec3  dv = q - (balls[i].xyz + s * ax);");
        _leaves.Line("float r2 = balls[i].w * balls[i].w;");
        _leaves.Line("float u  = 1.0 - dot(dv, dv) / r2;");
        _leaves.Line();
        _leaves.Line("if (u <= 0.0) continue;");
        _leaves.Line();
        _leaves.Line("field += caps[i].w * u * u;");
        _leaves.Line("grad  -= caps[i].w * 4.0 * u * dv / r2;");
        _leaves.Close();
        _leaves.Line();
        _leaves.Line("// Negative inside, matching every other primitive here.");
        _leaves.Line($"float f = {GlslWriter.Float(threshold)} - field;");
        _leaves.Line();
        _leaves.Line("// THIS IS NOT A DISTANCE, and no amount of care makes it one. It is the first-order");
        _leaves.Line("// estimate f / |grad f|, which overshoots wherever the field curves, and the understep");
        _leaves.Line("// coefficient only makes that rarer. Holes in a blob under --sdf are this line.");
        _leaves.Line("// Segment tracing's local Lipschitz bounds are the principled fix; see");
        _leaves.Line("// documents/raymarching.md.");
        _leaves.Line("float g = length(grad);");
        _leaves.Line("if (g < TINY) return INF;");
        _leaves.Line();
        _leaves.Line($"return (f / g) * BLOB_UNDERSTEP * {scale};");
    }

    // --- The map and the marcher ------------------------------------------------------------

    /// <summary>
    /// The whole scene as one signed distance function, with the nearest leaf travelling beside
    /// the distance.
    /// </summary>
    /// <remarks>
    /// The leaf code is <c>±(index + 1)</c>, borrowing the span path's own encoding: the sign says
    /// the surface came from a subtracted operand and its normal has to be negated. Zero would
    /// mean "no surface", which cannot happen here, and is reserved anyway.
    /// </remarks>
    private void WriteMap(GlslWriter w)
    {
        w.Line("// --- The scene as one field --------------------------------------------------------");
        w.Line("// union -> min, intersection -> max, difference -> max(a, -b). Only the first is an");
        w.Line("// exact distance; the other two are lower bounds, which is all sphere tracing needs.");
        w.Line("// The leaf code rides alongside as +/-(index + 1), the same encoding the span path uses");
        w.Line("// for a surface reference, so a hit can name its primitive and keep an exact normal.");
        w.Line();
        w.Open("float mapScene(vec3 p, out int leaf)");

        foreach (string line in _body.ToString().TrimEnd('\n').Split('\n'))
        {
            w.Line(line);
        }

        if (_roots.Count == 0)
        {
            w.Line("leaf = 0;");
            w.Line("return INF;");
            w.Close();
            w.Line();
            return;
        }

        string distance = _roots[0].Distance;
        string code = _roots[0].Leaf;

        for (int i = 1; i < _roots.Count; i++)
        {
            string d = Fresh("r");
            string l = Fresh("k");

            w.Line($"float {d} = min({distance}, {_roots[i].Distance});");
            w.Line($"int   {l} = {distance} <= {_roots[i].Distance} ? {code} : {_roots[i].Leaf};");

            distance = d;
            code = l;
        }

        w.Line();
        w.Line($"leaf = {code};");
        w.Line($"return {distance};");
        w.Close();
        w.Line();
    }

    /// <summary>
    /// The sphere tracer, answering the same question <c>traceScene</c> always answered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The loop is bounded by a uniform and that is not a style choice.</b> The driver unrolls
    /// every loop whose bound is a compile-time constant, and each copy would carry the whole
    /// scene's <c>mapScene</c> inlined into it. A constant bound of 128 is 128 scenes.
    /// documents/gpu-backends.md is where that lesson was paid for.
    /// </para>
    /// <para>
    /// Two variants share this body and one <c>mapScene</c>, chosen by <c>CHROMA_SDF_ENHANCED</c>,
    /// so what is being compared is the marching rule and nothing else.
    /// </para>
    /// </remarks>
    private static void WriteTraceScene(GlslWriter w)
    {
        w.Line("// --- The marcher -------------------------------------------------------------------");
        w.Line();
        w.Line("// Bounded by a uniform, never by a constant: a constant bound is unrolled and every");
        w.Line("// copy carries the whole scene. See the remarks on Codegen/SdfEmitter.");
        w.Line("uniform int uMarchSteps;");
        w.Line();
        w.Line("// Where the march is close enough to call it a hit.");
        w.Line("//");
        w.Line("// It has to stay ABOVE the offset a spawned ray starts at, or a bounce ray reports the");
        w.Line("// surface it just left as a hit; the symptom is acne everywhere, and it reads as a");
        w.Line("// lighting bug. This was an absolute 1e-4 chosen to sit under an absolute 1e-3, and both");
        w.Line("// of those numbers are gone -- so it is derived from the same place the offsets now are");
        w.Line("// and tracks them by construction rather than by a pair of comments agreeing.");
        w.Line("//");
        w.Line("// Eight tolerances rather than one because mapScene is a COMPOSITION: every min, max and");
        w.Line("// transform down the tree adds its own rounding to the distance that comes back, where");
        w.Line("// tTolerance counts the roundings behind a single endpoint.");
        w.Open("float marchTolerance(float t)");
        w.Line("return 8.0 * tTolerance(t);");
        w.Close();
        w.Line();
        w.Line("// Over-relaxation. 1.0 is the plain step; the published range is [1, 2).");
        w.Line("const float MARCH_OMEGA = 1.2;");
        w.Line();
        w.Line("// A far clip. Camera rays arrive with maxT = INF and a ray that escapes upward would");
        w.Line("// otherwise spend every step it has climbing away from a scene it already left.");
        w.Line("const float MARCH_FAR = 1.0e4;");
        w.Line();

        w.Open("Hit traceScene(vec3 ro, vec3 rd, bool anyHit, float maxT)");
        w.Line("Hit best = noHit();");
        w.Line();
        w.Line("// The ray's own share of every tolerance below, set once for the whole march. The");
        w.Line("// interval backend's traceScene opens with the same line and for the same reason; see");
        w.Line("// the remarks on gTScale in raytrace.glsl.");
        w.Line("gTScale = max(max(abs(ro.x), abs(ro.y)), abs(ro.z)) / max(length(rd), TINY);");
        w.Line();
        w.Line("int code = 0;");
        w.Line();
        w.Line("// Which side the ray starts on. Marching on sgn * map means a ray that begins inside a");
        w.Line("// solid converges on where it LEAVES, which is what `entering` reports and what");
        w.Line("// refraction and the medium walk both need.");
        w.Line("float sgn = mapScene(ro, code) < 0.0 ? -1.0 : 1.0;");
        w.Line();
        w.Line("float t     = 0.0;");
        w.Line("float rCur  = 0.0;   // unbounding radius at the current point");
        w.Line("float rPrev = 0.0;   // and at the one before, for the planar extrapolation");
        w.Line("float taken = 0.0;   // the step that got us here");
        w.Line();
        w.Open("for (int i = 0; i < uMarchSteps; ++i)");
        w.Line("// The always-safe step is the unbounding radius itself: nothing can be nearer.");
        w.Line("float step_ = rCur;");
        w.Line();
        w.Line("#if CHROMA_SDF_ENHANCED");
        w.Line();
        w.Line("// Predict the next unbounding radius by assuming the surface is locally planar, which");
        w.Line("// is precisely the case a plain sphere trace handles worst. Balint and Valasek 2018.");
        w.Open("if (rPrev > 0.0)");
        w.Line("float denom = taken + rPrev - rCur;");
        w.Open("if (denom > TINY)");
        w.Line("float predicted = rCur * (taken * rPrev + rCur) / denom;");
        w.Line("step_ = max(rCur, rCur + MARCH_OMEGA * predicted);");
        w.Close();
        w.Close();
        w.Line();
        w.Line("#endif");
        w.Line();
        w.Line("float h = sgn * mapScene(ro + (t + step_) * rd, code);");
        w.Line();
        w.Line("#if CHROMA_SDF_ENHANCED");
        w.Line();
        w.Line("// The two unbounding spheres are disjoint, so a surface may sit in the gap between");
        w.Line("// them. Retake the step conservatively rather than risk having jumped a hit.");
        w.Open("if (step_ > rCur + h)");
        w.Line("step_ = rCur;");
        w.Line("h     = sgn * mapScene(ro + (t + step_) * rd, code);");
        w.Close();
        w.Line();
        w.Line("#endif");
        w.Line();
        w.Line("t    += step_;");
        w.Line("rPrev = rCur;");
        w.Line("rCur  = h;");
        w.Line("taken = step_;");
        w.Line();
        w.Line("if (t > min(maxT, MARCH_FAR)) break;");
        w.Line();
        w.Open("if (h < marchTolerance(t))");
        w.Line("// The march has converged. Everything the shading half reads is decided here, and");
        w.Line("// `flip` follows resolveRoot's rule exactly: a subtracted surface, or a surface seen");
        w.Line("// from behind because the ray began inside, turns the normal over. Both, and it does");
        w.Line("// not.");
        w.Line("best.found     = true;");
        w.Line("best.t         = t;");
        w.Line("best.primitive = abs(code) - 1;");
        w.Line("best.entering  = sgn > 0.0;");
        w.Line("best.flip      = (code < 0) != (sgn < 0.0);");
        w.Line("return best;");
        w.Close();
        w.Close();
        w.Line();
        w.Line("// anyHit asks a different question and this answers it the same way: a shadow ray that");
        w.Line("// runs out of steps or distance without converging has found nothing in the way.");
        w.Line("return best;");
        w.Close();
        w.Line();
    }

    // --- Helpers ----------------------------------------------------------------------------

    private string Fresh(string prefix) => $"{prefix}{_temporaries++}";

    private static void Paste(GlslWriter target, GlslWriter source)
    {
        string text = source.ToString().TrimEnd('\n');

        if (text.Length == 0)
        {
            return;
        }

        target.Line("// --- Leaves ----------------------------------------------------------------------");
        target.Line();

        foreach (string line in text.Split('\n'))
        {
            target.Line(line);
        }

        target.Line();
    }

    /// <summary>
    /// The largest singular value of a 3x3, by power iteration on its normal matrix.
    /// </summary>
    /// <remarks>
    /// Used to convert a distance measured in the primitive's own space back into world units,
    /// and it must never come out too small, which would let the march overstep. Power iteration
    /// converges from below, so the result is scaled up by a hair to land on the safe side.
    /// </remarks>
    private static float LargestSingularValue(Matrix4x4 m)
    {
        // M^T M, symmetric positive semi-definite; its largest eigenvalue is sigmaMax squared.
        Vector3 r0 = new(m.M11, m.M12, m.M13);
        Vector3 r1 = new(m.M21, m.M22, m.M23);
        Vector3 r2 = new(m.M31, m.M32, m.M33);

        float a00 = r0.X * r0.X + r1.X * r1.X + r2.X * r2.X;
        float a01 = r0.X * r0.Y + r1.X * r1.Y + r2.X * r2.Y;
        float a02 = r0.X * r0.Z + r1.X * r1.Z + r2.X * r2.Z;
        float a11 = r0.Y * r0.Y + r1.Y * r1.Y + r2.Y * r2.Y;
        float a12 = r0.Y * r0.Z + r1.Y * r1.Z + r2.Y * r2.Z;
        float a22 = r0.Z * r0.Z + r1.Z * r1.Z + r2.Z * r2.Z;

        Vector3 v = Vector3.Normalize(new Vector3(1f, 1f, 1f));
        float eigen = 0f;

        for (int i = 0; i < 40; i++)
        {
            Vector3 next = new(
                a00 * v.X + a01 * v.Y + a02 * v.Z,
                a01 * v.X + a11 * v.Y + a12 * v.Z,
                a02 * v.X + a12 * v.Y + a22 * v.Z);

            float length = next.Length();

            if (length < 1e-20f)
            {
                break;
            }

            v = next / length;
            eigen = length;
        }

        float sigma = MathF.Sqrt(eigen);

        // Never zero: a singular matrix was refused above, and a tiny value here would emit an
        // enormous scale rather than an obviously wrong one.
        return sigma > 1e-12f ? sigma * 1.0001f : 1f;
    }

    private static Matrix4x4 UpBasis(Vector3 v)
    {
        Vector3 helper = MathF.Abs(v.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX;
        Vector3 u = Vector3.Normalize(Vector3.Cross(helper, v));
        Vector3 w = Vector3.Cross(v, u);

        return new Matrix4x4(
            u.X, u.Y, u.Z, 0f,
            v.X, v.Y, v.Z, 0f,
            w.X, w.Y, w.Z, 0f,
            0f, 0f, 0f, 1f);
    }

    private static Matrix4x4 AxisBasis(Vector3 from, Vector3 to, out float height)
    {
        Vector3 axis = to - from;
        height = axis.Length();
        return UpBasis(axis / height);
    }

    /// <summary>
    /// The same header, ranges and edges the span backend writes. Both backends shade through
    /// the one <c>primitiveNormal</c> in raytrace.glsl, so the buffer is not this backend's to
    /// lay out differently.
    /// </summary>
    private void AppendEdges(IReadOnlyList<Vector2> points, IReadOnlyList<int> contours, bool smooth)
    {
        _shapeOffset = _shapeData.Count / GpuLayout.ShapeStride;

        _shapeData.Add(contours.Count);
        _shapeData.Add(smooth ? 1f : 0f);
        _shapeData.Add(0f);
        _shapeData.Add(0f);

        int start = 0;

        foreach (int size in contours)
        {
            _shapeData.Add(start);
            _shapeData.Add(size);
            _shapeData.Add(0f);
            _shapeData.Add(0f);

            start += size;
        }

        start = 0;

        foreach (int size in contours)
        {
            for (int i = 0; i < size; i++)
            {
                Vector2 a = points[start + i];
                Vector2 b = points[start + ((i + 1) % size)];

                _shapeData.Add(a.X);
                _shapeData.Add(a.Y);
                _shapeData.Add(b.X);
                _shapeData.Add(b.Y);
            }

            start += size;
        }
    }

    private void AppendRows(Matrix4x4 m)
    {
        _primitives.Add(m.M11); _primitives.Add(m.M12); _primitives.Add(m.M13); _primitives.Add(m.M14);
        _primitives.Add(m.M21); _primitives.Add(m.M22); _primitives.Add(m.M23); _primitives.Add(m.M24);
        _primitives.Add(m.M31); _primitives.Add(m.M32); _primitives.Add(m.M33); _primitives.Add(m.M34);
        _primitives.Add(m.M41); _primitives.Add(m.M42); _primitives.Add(m.M43); _primitives.Add(m.M44);
    }

    private int InternMaterial(Material material)
    {
        if (_materialIndices.TryGetValue(material, out int existing))
        {
            return existing;
        }

        int index = _materials.Count / GpuLayout.MaterialStride;

        _materials.Add(material.Color.X);
        _materials.Add(material.Color.Y);
        _materials.Add(material.Color.Z);
        _materials.Add(material.Roughness);

        _materials.Add(material.Emission.X);
        _materials.Add(material.Emission.Y);
        _materials.Add(material.Emission.Z);
        _materials.Add(material.Metallic);

        _materials.Add(material.Absorption.X);
        _materials.Add(material.Absorption.Y);
        _materials.Add(material.Absorption.Z);

        float transmission = material.Metallic > 0f ? 0f : material.Transmission;
        _materials.Add(transmission);

        _materials.Add(material.Ior);
        _materials.Add(transmission > 0f ? material.Scattering : 0f);
        _materials.Add(material.Anisotropy);
        _materials.Add(0f);

        _materialIndices[material] = index;
        return index;
    }
}
