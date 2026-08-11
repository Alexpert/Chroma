using System.Numerics;
using Chroma.Core.Compilation;
using Chroma.Core.Model.Geometry;
using Chroma.Core.Model.Geometry.Operations;
using Chroma.Core.Model.Geometry.Primitives;
using Chroma.Core.Model.Materials;
using Chroma.Core.Sdl.Source;

// System.Numerics has a Plane of its own — a mathematical plane, not a solid — and this file
// needs its matrices. The alias says which one is meant once, rather than at each mention.
using Plane = Chroma.Core.Model.Geometry.Primitives.Plane;

namespace Chroma.Core.Codegen;

/// <summary>
/// Walks the solid tree and writes the GLSL that evaluates it.
/// </summary>
/// <remarks>
/// <para>
/// This replaces the post-order tape and its stack machine. A tape had to be walked by a loop
/// over an array of span lists indexed by a runtime stack pointer, and that dynamic indexing is
/// what forced the whole stack into local memory — the single largest cost in the shader it
/// replaces. Generated code has no stack: the tree becomes nested calls over <b>named locals</b>,
/// each sized to its own node's worst case, and the register allocator can see all of it.
/// </para>
/// <para>
/// Every transform is folded and inverted here, as it always was, and emitted as a <c>const
/// mat4</c> rather than four texture fetches. Every primitive is still evaluated in its
/// canonical form — unit sphere, [-1,1] box, unit cylinder along +Y — so a non-uniform scale on
/// the canonical sphere still gives an ellipsoid for free.
/// </para>
/// <para>
/// The primitive and material tables survive, but for one purpose only: shading. A normal is
/// recomputed once per hit, from the one surface that turned out to be visible, and generating
/// a branch per leaf for that would grow the source with the scene while saving a fetch that
/// happens once per bounce. Spans are the hot path and are generated; normals are not and are
/// not.
/// </para>
/// </remarks>
internal sealed class GeometryEmitter : ISolidVisitor<GeometryEmitter.Node>
{
    /// <summary>A subtree's result: the local holding its spans, how many it can hold, and where it is.</summary>
    internal readonly record struct Node(string Variable, int Spans, Aabb Bounds);

    /// <summary>The canonical box, sphere and every other centred primitive: [-1, 1] on each axis.</summary>
    private static readonly Aabb CanonicalCube = new(new Vector3(-1f), new Vector3(1f));

    /// <summary>The canonical cylinder and cone: unit radius about +Y, from y = 0 to y = 1.</summary>
    private static readonly Aabb CanonicalColumn = new(new Vector3(-1f, 0f, -1f), new Vector3(1f, 1f, 1f));

    private readonly SpanLibrary _spans = new();
    private readonly GlslWriter _leaves = new();
    private readonly GlslWriter _shapes = new();
    private readonly List<float> _primitives = [];
    private readonly List<float> _materials = [];
    private readonly List<float> _shapeData = [];
    private readonly Dictionary<Material, int> _materialIndices = [];
    private readonly List<Node> _roots = [];

    private GlslWriter _body = new();
    private Matrix4x4 _ancestorTransform = Matrix4x4.Identity;
    private Material? _inheritedMaterial;
    private int _locals;
    private bool _failed;

    /// <summary>Texel index the shape data of the leaf being emitted starts at.</summary>
    /// <remarks>
    /// The shape buffer is written by the visit method before it calls <see cref="EmitLeaf"/>,
    /// so this is captured on the way in and read on the way out.
    /// </remarks>
    private int _shapeOffset;

    private readonly LeafEmitter _leafEmitter;
    private readonly DiagnosticBag _diagnostics;

    public GeometryEmitter(DiagnosticBag diagnostics)
    {
        _diagnostics = diagnostics;
        _leafEmitter = new LeafEmitter(_spans);
    }

    public IReadOnlyList<float> Primitives => _primitives;

    public IReadOnlyList<float> Materials => _materials;

    public IReadOnlyList<float> Shapes => _shapeData;

    public int LeafCount => _primitives.Count / GpuLayout.PrimitiveStride;

    /// <summary>Widest span list any root produces, for the console line only.</summary>
    public int WidestRoot => _roots.Count == 0 ? 0 : _roots.Max(root => root.Spans);

    /// <summary>Emits one top-level solid as its own function, to be resolved on its own.</summary>
    /// <remarks>
    /// Roots are implicitly unioned but are resolved one at a time rather than merged, exactly
    /// as the tape did. Merging would make a scene of nine separate spheres cost a nine-span
    /// list, where nine independent roots each cost one.
    /// </remarks>
    public void EmitRoot(Solid solid)
    {
        int index = _roots.Count;

        _body = new GlslWriter();
        _locals = 0;

        Node node = Descend(solid);

        if (_failed)
        {
            return;
        }

        // Asked for here rather than in WriteTraceScene: the library is written out before the
        // trace function is, so anything the trace function needs has to be registered while
        // the roots are still being walked.
        _spans.Root(node.Spans);

        _shapes.Line($"// root {index}, {node.Spans} span{(node.Spans == 1 ? "" : "s")} at worst");
        _shapes.Open($"void shape{index}(vec3 ro, vec3 rd, out {_spans.Type(node.Spans)} result)");

        foreach (string line in _body.ToString().TrimEnd('\n').Split('\n'))
        {
            _shapes.Line(line);
        }

        _shapes.Line($"result = {node.Variable};");
        _shapes.Close();
        _shapes.Line();

        _roots.Add(node with { Variable = $"shape{index}" });
    }

    /// <summary>Assembles the whole generated block, in declaration order.</summary>
    public string Build()
    {
        var w = new GlslWriter();

        w.Line("// ===== generated geometry =========================================================");
        w.Line("// Emitted for this scene and no other. Written by Chroma.Core.Codegen; the maths it");
        w.Line("// calls is hand-written above. Run with --emit-shader to read the whole file.");
        w.Line();

        _spans.WriteTo(w);
        _leafEmitter.WriteHelpers(w);

        w.Line("// --- Leaves ----------------------------------------------------------------------");
        w.Line();
        Paste(w, _leaves);

        w.Line("// --- Roots -----------------------------------------------------------------------");
        w.Line();
        Paste(w, _shapes);

        WriteTraceScene(w);
        return w.ToString();
    }

    /// <summary>Copies one writer's lines into another, preserving their own indentation.</summary>
    private static void Paste(GlslWriter target, GlslWriter source)
    {
        foreach (string line in source.ToString().TrimEnd('\n').Split('\n'))
        {
            target.Line(line);
        }

        target.Line();
    }

    /// <summary>
    /// The whole scene for one ray: every root in turn, each behind its own bounding box.
    /// </summary>
    /// <remarks>
    /// The guard is a plain <c>if</c> on a constant box rather than the tape's jump instruction,
    /// so it costs nothing to have and nothing to skip. A root with a <c>plane</c> in it is
    /// unbounded and gets no guard at all rather than one that always answers yes.
    /// </remarks>
    private void WriteTraceScene(GlslWriter w)
    {
        w.Line("// --- The scene -------------------------------------------------------------------");
        w.Line();
        w.Line("// anyHit answers a different question -- \"is anything in the way at all\" -- and returns");
        w.Line("// as soon as it knows. It deliberately does NOT apply the \"started inside\" rule: a");
        w.Line("// surface must not shadow itself.");
        w.Open("Hit traceScene(vec3 ro, vec3 rd, bool anyHit, float maxT)");
        w.Line("Hit best = noHit();");
        w.Line();

        for (int i = 0; i < _roots.Count; i++)
        {
            Node root = _roots[i];
            bool guarded = root.Bounds.IsFinite && !root.Bounds.IsEmpty;

            if (guarded)
            {
                w.Open(
                    $"if (boundHit(ro, rd, {GlslWriter.Vec3(root.Bounds.Min)}, "
                    + $"{GlslWriter.Vec3(root.Bounds.Max)}, min(maxT, best.t)))");
            }
            else
            {
                w.Open("");
            }

            w.Line($"{_spans.Type(root.Spans)} list;");
            w.Line($"{root.Variable}(ro, rd, list);");
            w.Line();
            w.Open("if (anyHit)");
            w.Open($"if (rootOccludes{_spans.Root(root.Spans)}(list, maxT))");
            w.Line("best.found = true;");
            w.Line("return best;");
            w.Close();
            w.Close();
            w.Open("else");
            w.Line($"resolveRoot{_spans.Root(root.Spans)}(list, best);");
            w.Close();
            w.Close();
        }

        w.Line();
        w.Line("return best;");
        w.Close();
    }

    /// <summary>Walks one subtree, carrying the ancestors' transform and material down it.</summary>
    private Node Descend(Solid solid)
    {
        Matrix4x4 savedTransform = _ancestorTransform;
        Material? savedMaterial = _inheritedMaterial;

        // Row-vector convention: a point in the child's space is transformed by the child
        // first, then by its ancestors, so the child multiplies on the left.
        _ancestorTransform = solid.Transform.Matrix * _ancestorTransform;
        _inheritedMaterial = solid.Material ?? _inheritedMaterial;

        Node node = solid.Accept(this);

        _ancestorTransform = savedTransform;
        _inheritedMaterial = savedMaterial;
        return node;
    }

    public Node VisitSphere(Sphere sphere) => EmitLeaf(
        sphere,
        PrimitiveKind.Sphere,
        Matrix4x4.CreateScale(sphere.Radius)
        * Matrix4x4.CreateTranslation(sphere.Center)
        * _ancestorTransform,
        CanonicalCube);

    public Node VisitBox(Box box) => EmitLeaf(
        box,
        PrimitiveKind.Box,
        Matrix4x4.CreateScale((box.Max - box.Min) * 0.5f)
        * Matrix4x4.CreateTranslation((box.Max + box.Min) * 0.5f)
        * _ancestorTransform,
        CanonicalCube);

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
            CanonicalColumn);
    }

    /// <summary>
    /// A cone, canonicalised to radius 1 at <c>y = 0</c> tapering to <c>capRadius</c> at
    /// <c>y = 1</c>. The taper is the one number no affine transform can absorb, so it travels
    /// as a literal in the generated call.
    /// </summary>
    public Node VisitCone(Cone cone)
    {
        Vector3 basePoint = cone.Base;
        Vector3 capPoint = cone.Cap;
        float baseRadius = cone.BaseRadius;
        float capRadius = cone.CapRadius;

        // Keeping the wider end as the base bounds the ratio to [0, 1]; swapping the two ends
        // describes the same solid, so the swap costs nothing.
        if (capRadius > baseRadius)
        {
            (basePoint, capPoint) = (capPoint, basePoint);
            (baseRadius, capRadius) = (capRadius, baseRadius);
        }

        Matrix4x4 basis = AxisBasis(basePoint, capPoint, out float height);

        return EmitLeaf(
            cone,
            PrimitiveKind.Cone,
            Matrix4x4.CreateScale(baseRadius, height, baseRadius)
            * basis
            * Matrix4x4.CreateTranslation(basePoint)
            * _ancestorTransform,
            CanonicalColumn,
            capRadius / baseRadius);
    }

    /// <summary>
    /// A half-space, canonicalised to <c>y &lt;= 0</c>. The one primitive with no bounding box:
    /// it extends to infinity in five directions, so every root containing one is unguarded.
    /// </summary>
    public Node VisitPlane(Plane plane)
    {
        Vector3 normal = Vector3.Normalize(plane.Normal);

        return EmitLeaf(
            plane,
            PrimitiveKind.Plane,
            UpBasis(normal)
            * Matrix4x4.CreateTranslation(normal * plane.Distance)
            * _ancestorTransform,
            Aabb.Unbounded);
    }

    /// <summary>
    /// A torus, canonicalised to major radius 1 in the XZ plane. Two spans: a ray can go in one
    /// side of the ring and out the other.
    /// </summary>
    public Node VisitTorus(Torus torus)
    {
        float minor = torus.MinorRadius / torus.MajorRadius;

        return EmitLeaf(
            torus,
            PrimitiveKind.Torus,
            Matrix4x4.CreateScale(torus.MajorRadius)
            * Matrix4x4.CreateTranslation(torus.Center)
            * _ancestorTransform,
            // Flat: the ring reaches 1 + minor across and only minor deep.
            new Aabb(
                new Vector3(-(1f + minor), -minor, -(1f + minor)),
                new Vector3(1f + minor, minor, 1f + minor)),
            minor,
            spans: 2);
    }

    /// <summary>
    /// A prism, canonicalised to its contour in XZ swept from <c>y = 0</c> to <c>y = 1</c>.
    /// A ray crosses each extruded wall at most once, so <c>edges / 2</c> spans is exact.
    /// </summary>
    public Node VisitPrism(Prism prism)
    {
        AppendEdges(prism.Points);
        float height = prism.Top - prism.Bottom;

        Aabb contour = Aabb.Empty;
        foreach (Vector2 point in prism.Points)
        {
            contour = Aabb.Union(contour, new Aabb(
                new Vector3(point.X, 0f, point.Y),
                new Vector3(point.X, 1f, point.Y)));
        }

        return EmitLeaf(
            prism,
            PrimitiveKind.Prism,
            Matrix4x4.CreateScale(1f, height, 1f)
            * Matrix4x4.CreateTranslation(0f, prism.Bottom, 0f)
            * _ancestorTransform,
            contour,
            paramB: prism.Points.Count,
            spans: Math.Max(1, prism.Points.Count / 2),
            points: prism.Points);
    }

    /// <summary>
    /// A lathe. Its canonical form is itself: the outline is already in the units the file
    /// wrote, so only the ancestors' transform applies.
    /// </summary>
    /// <remarks>
    /// The span count is the segment count, exactly, because each band can be crossed on both
    /// sides of the axis and the crossings pair. It is no longer clamped to a global maximum —
    /// which is what let a 24-segment lathe render as a solid with a slice missing.
    /// </remarks>
    public Node VisitLathe(Lathe lathe)
    {
        AppendEdges(lathe.Points);

        float radius = 0f;
        float low = float.PositiveInfinity;
        float high = float.NegativeInfinity;

        foreach (Vector2 point in lathe.Points)
        {
            radius = MathF.Max(radius, MathF.Abs(point.X));
            low = MathF.Min(low, point.Y);
            high = MathF.Max(high, point.Y);
        }

        return EmitLeaf(
            lathe,
            PrimitiveKind.Lathe,
            _ancestorTransform,
            new Aabb(new Vector3(-radius, low, -radius), new Vector3(radius, high, radius)),
            // The shading path still reads the smooth flag from the sign of the segment count.
            paramB: lathe.Smooth ? -lathe.Points.Count : lathe.Points.Count,
            spans: lathe.Points.Count,
            points: lathe.Points);
    }

    /// <summary>A blob: a threshold texel followed by two texels per component.</summary>
    public Node VisitBlob(Blob blob)
    {
        _shapeOffset = _shapeData.Count / GpuLayout.ShapeStride;

        _shapeData.Add(blob.Threshold);
        _shapeData.Add(0f);
        _shapeData.Add(0f);
        _shapeData.Add(0f);

        // A component's field is exactly zero at its own radius and beyond, which is what makes
        // a blob local — and what makes the union of its components' spheres a true bound.
        Aabb bounds = Aabb.Empty;
        var balls = new List<Vector4>(blob.Components.Count);
        var strengths = new List<float>(blob.Components.Count);

        foreach (BlobSphere component in blob.Components)
        {
            _shapeData.Add(component.Center.X);
            _shapeData.Add(component.Center.Y);
            _shapeData.Add(component.Center.Z);
            _shapeData.Add(component.Radius);

            _shapeData.Add(component.Strength);
            _shapeData.Add(0f);
            _shapeData.Add(0f);
            _shapeData.Add(0f);

            balls.Add(new Vector4(component.Center, component.Radius));
            strengths.Add(component.Strength);
            bounds = Aabb.Union(bounds, Aabb.AroundSphere(component.Center, component.Radius));
        }

        return EmitLeaf(
            blob,
            PrimitiveKind.Blob,
            _ancestorTransform,
            bounds,
            blob.Threshold,
            paramB: blob.Components.Count,
            spans: blob.Components.Count,
            balls: balls,
            strengths: strengths);
    }

    /// <summary>A sphere sweep: an open path of <c>n</c> spheres giving <c>n - 1</c> hulls.</summary>
    public Node VisitSphereSweep(SphereSweep sweep)
    {
        _shapeOffset = _shapeData.Count / GpuLayout.ShapeStride;

        Aabb bounds = Aabb.Empty;

        foreach (Vector4 sphere in sweep.Spheres)
        {
            _shapeData.Add(sphere.X);
            _shapeData.Add(sphere.Y);
            _shapeData.Add(sphere.Z);
            _shapeData.Add(sphere.W);

            bounds = Aabb.Union(
                bounds,
                Aabb.AroundSphere(new Vector3(sphere.X, sphere.Y, sphere.Z), sphere.W));
        }

        return EmitLeaf(
            sweep,
            PrimitiveKind.SphereSweep,
            _ancestorTransform,
            bounds,
            paramB: sweep.Spheres.Count,
            spans: sweep.Spheres.Count - 1,
            balls: sweep.Spheres);
    }

    public Node VisitUnion(Union union) => EmitOperation(union, "Union");

    public Node VisitIntersection(Intersection intersection) => EmitOperation(intersection, "Intersection");

    public Node VisitDifference(Difference difference) => EmitOperation(difference, "Difference");

    /// <summary>
    /// One operator, binarised into a left-associated chain: <c>union { a b c }</c> becomes
    /// <c>(a ∪ b) ∪ c</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The span counts are exact worst cases and nothing clamps them. Union interleaves without
    /// coalescing anything, so the counts add. Difference is <c>A ∩ complement(B)</c>, and
    /// complementing <c>B</c> gives <c>|B| + 1</c> intervals, so the sweep emits at most
    /// <c>|A| + |B|</c>.
    /// </para>
    /// <para>
    /// Intersection is <c>|A| + |B| - 1</c>, not <c>min(|A|, |B|)</c>. The sweep advances one
    /// pointer per emitted span, so a single long span meeting three short ones produces three —
    /// the minimum was never a bound, and it went unnoticed only because every list was eight
    /// spans wide whatever the scene said. Sizing a list from its own node is what made it
    /// visible.
    /// </para>
    /// </remarks>
    private Node EmitOperation(CsgOperation operation, string name)
    {
        IReadOnlyList<Solid> operands = operation.Operands;

        // The binder rejects an operator with fewer than two operands, so the loop always runs.
        Node accumulated = Descend(operands[0]);

        for (int i = 1; i < operands.Count; i++)
        {
            Node right = Descend(operands[i]);

            int spans = name switch
            {
                "Union" => accumulated.Spans + right.Spans,
                "Intersection" => accumulated.Spans + right.Spans - 1,
                _ => accumulated.Spans + right.Spans,
            };

            string call = name switch
            {
                "Union" => _spans.Union(accumulated.Spans, right.Spans, spans),
                "Intersection" => _spans.Intersection(accumulated.Spans, right.Spans, spans),
                _ => _spans.Difference(accumulated.Spans, right.Spans, spans),
            };

            string variable = $"v{_locals++}";
            _body.Line($"{_spans.Type(spans)} {variable};");
            _body.Line($"{call}({accumulated.Variable}, {right.Variable}, {variable});");
            _body.Line();

            // Each operator bounds its result differently, and each is the tightest box that is
            // still a bound. Difference keeps the left operand's alone: removing material can
            // only shrink a solid.
            Aabb bounds = name switch
            {
                "Union" => Aabb.Union(accumulated.Bounds, right.Bounds),
                "Intersection" => Aabb.Intersect(accumulated.Bounds, right.Bounds),
                _ => accumulated.Bounds,
            };

            accumulated = new Node(variable, spans, bounds);
        }

        return accumulated;
    }

    /// <param name="canonicalBounds">
    /// A box around the primitive in its own canonical space, which this transforms into the
    /// world. Passed in rather than derived from <paramref name="kind"/>: the primitives whose
    /// box is not a constant read it off the points they were given.
    /// </param>
    private Node EmitLeaf(
        Solid solid,
        PrimitiveKind kind,
        Matrix4x4 toWorld,
        Aabb canonicalBounds,
        float paramA = 0f,
        float paramB = 0f,
        int spans = 1,
        IReadOnlyList<Vector2>? points = null,
        IReadOnlyList<Vector4>? balls = null,
        IReadOnlyList<float>? strengths = null)
    {
        if (!Matrix4x4.Invert(toWorld, out Matrix4x4 toLocal))
        {
            _failed = true;
            _diagnostics.Error(
                solid.Origin,
                $"'{solid.Kind.ToLowerInvariant()}' has a transform that cannot be inverted; "
                + "a zero scale collapses the solid to nothing");

            return new Node("v0", 1, Aabb.Empty);
        }

        int index = LeafCount;

        // The primitive record is unchanged and is still uploaded, for the shading path only:
        // a normal is recomputed once per hit, from whichever surface turned out to be visible.
        // Its two parameter slots mean what they always did — a ratio for the cone and the
        // torus, an offset and a count into the shape buffer for the four defined by a list —
        // because the shading code that reads them is unchanged. The generated span code reads
        // neither: its taper, its minor radius and its threshold are literals.
        bool listShaped = kind is PrimitiveKind.Prism or PrimitiveKind.Lathe
            or PrimitiveKind.Blob or PrimitiveKind.SphereSweep;

        _primitives.Add((float)kind);
        _primitives.Add(InternMaterial(_inheritedMaterial ?? Material.Default));
        _primitives.Add(listShaped ? _shapeOffset : paramA);
        _primitives.Add(paramB);
        AppendRows(toLocal);

        _leafEmitter.Write(_leaves, new LeafPlan(
            index,
            kind,
            spans,
            toLocal,
            paramA,
            points ?? [],
            balls ?? [],
            strengths ?? [],
            $"{solid.Kind.ToLowerInvariant()} — leaf {index}"));

        string variable = $"v{_locals++}";
        _body.Line($"{_spans.Type(spans)} {variable};");
        _body.Line($"leaf{index}(ro, rd, {variable});");
        _body.Line();

        return new Node(variable, spans, canonicalBounds.Transformed(toWorld));
    }

    /// <summary>
    /// Writes a closed contour as one texel per edge, and remembers where it started.
    /// </summary>
    /// <remarks>
    /// Still uploaded even though the span path now carries the same points as a <c>const</c>
    /// array: the shading path walks the contour to build a normal, and that is one evaluation
    /// per hit rather than one per ray per edge.
    /// </remarks>
    private void AppendEdges(IReadOnlyList<Vector2> points)
    {
        _shapeOffset = _shapeData.Count / GpuLayout.ShapeStride;

        for (int i = 0; i < points.Count; i++)
        {
            Vector2 a = points[i];
            Vector2 b = points[(i + 1) % points.Count];

            _shapeData.Add(a.X);
            _shapeData.Add(a.Y);
            _shapeData.Add(b.X);
            _shapeData.Add(b.Y);
        }
    }

    /// <summary>
    /// A rotation whose local <c>+Y</c> lands on <paramref name="v"/>, which must be unit length.
    /// </summary>
    private static Matrix4x4 UpBasis(Vector3 v)
    {
        // The helper vector is picked away from the axis so the cross product stays well
        // conditioned. A vertical axis is the common case and would be the degenerate one if
        // the helper were always +Y.
        Vector3 helper = MathF.Abs(v.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX;
        Vector3 u = Vector3.Normalize(Vector3.Cross(helper, v));
        Vector3 w = Vector3.Cross(v, u);

        // Rows are the images of the local axes, so local +Y lands on v.
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

        // A metal has no transmission lobe, so the shader would ignore it anyway. Zeroing it
        // here means "does this scene need transmissive shadow rays" stays a look at the table.
        float transmission = material.Metallic > 0f ? 0f : material.Transmission;
        _materials.Add(transmission);

        _materials.Add(material.Ior);

        // Same reasoning one step further: a medium only exists inside a solid light can get
        // into, so an opaque material's scattering is zeroed here.
        _materials.Add(transmission > 0f ? material.Scattering : 0f);
        _materials.Add(material.Anisotropy);
        _materials.Add(0f);

        _materialIndices[material] = index;
        return index;
    }
}
