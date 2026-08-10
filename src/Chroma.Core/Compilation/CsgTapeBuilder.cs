using System.Numerics;
using Chroma.Core.Model.Geometry;
using Chroma.Core.Model.Geometry.Operations;
using Chroma.Core.Model.Geometry.Primitives;
using Chroma.Core.Model.Materials;
using Chroma.Core.Sdl.Source;

// System.Numerics has a Plane of its own — a mathematical plane, not a solid — and every
// file here needs its matrices. The alias says which one is meant once, rather than at each
// mention.
using Plane = Chroma.Core.Model.Geometry.Primitives.Plane;

namespace Chroma.Core.Compilation;

/// <summary>
/// Flattens the solid tree into the GPU tape, baking every transform into one inverse
/// matrix per leaf and interning materials as it goes.
/// </summary>
/// <remarks>
/// <para>
/// The value the visitor returns is the subtree's <see cref="SpanBudget"/>, which is the
/// use the generic <see cref="ISolidVisitor{TResult}"/> was introduced for: the traversal
/// that emits the tape is the same one that has to size the shader's arrays.
/// </para>
/// <para>
/// Every primitive is evaluated in a canonical form — unit sphere, [-1,1] box, unit
/// cylinder along +Y — and its real dimensions live in the matrix. A non-uniform scale on
/// the canonical sphere therefore gives an ellipsoid for free.
/// </para>
/// <para>
/// Two shapes do not reduce to a canonical form under any affine map: a cone's taper and a
/// torus's minor radius survive as one number each, in the two parameter slots the primitive
/// record always had. Three more — prism, lathe and blob — are defined by a list rather than
/// by a formula, and that list goes to a separate shape buffer with an offset and a count in
/// the same two slots.
/// </para>
/// </remarks>
internal sealed class CsgTapeBuilder(DiagnosticBag diagnostics) : ISolidVisitor<SpanBudget>
{
    private readonly DiagnosticBag _diagnostics = diagnostics;
    private readonly List<int> _tape = [];
    private readonly List<float> _primitives = [];
    private readonly List<float> _materials = [];
    private readonly List<float> _shapes = [];
    private readonly Dictionary<Material, int> _materialIndices = [];

    // Accumulated down the tree and restored on the way back up, the same shape as the
    // hierarchy printer's prefix handling.
    private Matrix4x4 _ancestorTransform = Matrix4x4.Identity;
    private Material? _inheritedMaterial;
    private bool _budgetExceeded;

    public IReadOnlyList<int> Tape => _tape;

    public IReadOnlyList<float> Primitives => _primitives;

    public IReadOnlyList<float> Materials => _materials;

    public IReadOnlyList<float> Shapes => _shapes;

    /// <summary>Walks one root and returns its budget.</summary>
    public SpanBudget Descend(Solid solid)
    {
        Matrix4x4 savedTransform = _ancestorTransform;
        Material? savedMaterial = _inheritedMaterial;

        // Row-vector convention: a point in the child's space is transformed by the child
        // first, then by its ancestors, so the child multiplies on the left.
        _ancestorTransform = solid.Transform.Matrix * _ancestorTransform;
        _inheritedMaterial = solid.Material ?? _inheritedMaterial;

        SpanBudget budget = solid.Accept(this);

        _ancestorTransform = savedTransform;
        _inheritedMaterial = savedMaterial;
        return budget;
    }

    public SpanBudget VisitSphere(Sphere sphere) => EmitLeaf(
        sphere,
        PrimitiveKind.Sphere,
        Matrix4x4.CreateScale(sphere.Radius)
        * Matrix4x4.CreateTranslation(sphere.Center)
        * _ancestorTransform);

    public SpanBudget VisitBox(Box box) => EmitLeaf(
        box,
        PrimitiveKind.Box,
        Matrix4x4.CreateScale((box.Max - box.Min) * 0.5f)
        * Matrix4x4.CreateTranslation((box.Max + box.Min) * 0.5f)
        * _ancestorTransform);

    public SpanBudget VisitCylinder(Cylinder cylinder)
    {
        Matrix4x4 basis = AxisBasis(cylinder.Base, cylinder.Cap, out float height);

        return EmitLeaf(
            cylinder,
            PrimitiveKind.Cylinder,
            Matrix4x4.CreateScale(cylinder.Radius, height, cylinder.Radius)
            * basis
            * Matrix4x4.CreateTranslation(cylinder.Base)
            * _ancestorTransform);
    }

    /// <summary>
    /// A cone, canonicalised to radius 1 at <c>y = 0</c> tapering to <c>capRadius</c> at
    /// <c>y = 1</c>.
    /// </summary>
    /// <remarks>
    /// The taper is the one number no affine transform can absorb — scaling the canonical
    /// cone changes its size, never the ratio between its two ends — so it is the first
    /// value ever written into the primitive record's parameter slots. Keeping the *wider*
    /// end as the base is what bounds that ratio to [0, 1]; swapping the two ends describes
    /// the same solid, so the swap costs nothing.
    /// </remarks>
    public SpanBudget VisitCone(Cone cone)
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

        return EmitLeaf(
            cone,
            PrimitiveKind.Cone,
            Matrix4x4.CreateScale(baseRadius, height, baseRadius)
            * basis
            * Matrix4x4.CreateTranslation(basePoint)
            * _ancestorTransform,
            capRadius / baseRadius);
    }

    /// <summary>
    /// A half-space, canonicalised to <c>y &lt;= 0</c> with its surface through the origin.
    /// </summary>
    public SpanBudget VisitPlane(Plane plane)
    {
        Vector3 normal = Vector3.Normalize(plane.Normal);

        return EmitLeaf(
            plane,
            PrimitiveKind.Plane,
            UpBasis(normal)
            * Matrix4x4.CreateTranslation(normal * plane.Distance)
            * _ancestorTransform);
    }

    /// <summary>
    /// A torus, canonicalised to major radius 1 in the XZ plane.
    /// </summary>
    /// <remarks>
    /// Like the cone's taper, the ratio of the two radii is scale-invariant and has to
    /// travel as a parameter. Unlike every primitive before it, this one is not convex: two
    /// spans, for a ray that goes in one side of the ring and out the other.
    /// </remarks>
    public SpanBudget VisitTorus(Torus torus) => EmitLeaf(
        torus,
        PrimitiveKind.Torus,
        Matrix4x4.CreateScale(torus.MajorRadius)
        * Matrix4x4.CreateTranslation(torus.Center)
        * _ancestorTransform,
        torus.MinorRadius / torus.MajorRadius,
        spans: GpuLayout.SpansFor(PrimitiveKind.Torus, 0));

    /// <summary>
    /// A prism, canonicalised to its contour in XZ swept from <c>y = 0</c> to <c>y = 1</c>.
    /// </summary>
    public SpanBudget VisitPrism(Prism prism)
    {
        int offset = AppendEdges(prism.Points);
        float height = prism.Top - prism.Bottom;

        return EmitLeaf(
            prism,
            PrimitiveKind.Prism,
            Matrix4x4.CreateScale(1f, height, 1f)
            * Matrix4x4.CreateTranslation(0f, prism.Bottom, 0f)
            * _ancestorTransform,
            offset,
            prism.Points.Count,
            GpuLayout.SpansFor(PrimitiveKind.Prism, prism.Points.Count));
    }

    /// <summary>
    /// A lathe. Its canonical form is itself: the outline is already in the units the file
    /// wrote, so only the ancestors' transform applies.
    /// </summary>
    /// <remarks>
    /// The segment count travels <b>negated</b> when the outline came from a curve. There is
    /// no third parameter slot to put the flag in, and a count is never zero and never
    /// genuinely negative, so its sign is free storage — the shader takes the absolute value
    /// and reads the sign as "blend the normals across joints".
    /// </remarks>
    public SpanBudget VisitLathe(Lathe lathe)
    {
        int offset = AppendEdges(lathe.Points);
        int count = lathe.Points.Count;

        return EmitLeaf(
            lathe,
            PrimitiveKind.Lathe,
            _ancestorTransform,
            offset,
            lathe.Smooth ? -count : count,
            GpuLayout.SpansFor(PrimitiveKind.Lathe, count));
    }

    /// <summary>
    /// A blob: a threshold texel followed by two texels per component.
    /// </summary>
    /// <remarks>
    /// The threshold rides in the buffer rather than in a parameter slot because both slots
    /// are already spoken for by the offset and the count, and duplicating one number per
    /// component to avoid a header texel would cost more than the header does.
    /// </remarks>
    public SpanBudget VisitBlob(Blob blob)
    {
        int offset = _shapes.Count / GpuLayout.ShapeStride;

        _shapes.Add(blob.Threshold);
        _shapes.Add(0f);
        _shapes.Add(0f);
        _shapes.Add(0f);

        foreach (BlobSphere component in blob.Components)
        {
            _shapes.Add(component.Center.X);
            _shapes.Add(component.Center.Y);
            _shapes.Add(component.Center.Z);
            _shapes.Add(component.Radius);

            _shapes.Add(component.Strength);
            _shapes.Add(0f);
            _shapes.Add(0f);
            _shapes.Add(0f);
        }

        return EmitLeaf(
            blob,
            PrimitiveKind.Blob,
            _ancestorTransform,
            offset,
            blob.Components.Count,
            GpuLayout.SpansFor(PrimitiveKind.Blob, blob.Components.Count));
    }

    /// <summary>
    /// A sphere sweep: one texel per sphere, <c>(centre, radius)</c>.
    /// </summary>
    /// <remarks>
    /// Its canonical form is itself, like the lathe's. The path is open rather than closed, so
    /// <c>n</c> spheres give <c>n - 1</c> segments — which is why this does not reuse
    /// <see cref="AppendEdges"/>, whose whole job is to wrap the last point back to the first.
    /// </remarks>
    public SpanBudget VisitSphereSweep(SphereSweep sweep)
    {
        int offset = _shapes.Count / GpuLayout.ShapeStride;

        foreach (Vector4 sphere in sweep.Spheres)
        {
            _shapes.Add(sphere.X);
            _shapes.Add(sphere.Y);
            _shapes.Add(sphere.Z);
            _shapes.Add(sphere.W);
        }

        return EmitLeaf(
            sweep,
            PrimitiveKind.SphereSweep,
            _ancestorTransform,
            offset,
            sweep.Spheres.Count,
            GpuLayout.SpansFor(PrimitiveKind.SphereSweep, sweep.Spheres.Count));
    }

    public SpanBudget VisitUnion(Union union) => EmitOperation(union, TapeOpcode.Union);

    public SpanBudget VisitIntersection(Intersection intersection) =>
        EmitOperation(intersection, TapeOpcode.Intersection);

    public SpanBudget VisitDifference(Difference difference) =>
        EmitOperation(difference, TapeOpcode.Difference);

    /// <summary>
    /// Closes a root solid, so the shader knows where to resolve one and start the next.
    /// </summary>
    public void CloseRoot() => Emit(TapeOpcode.EndRoot, 0);

    /// <summary>
    /// Emits one operator, binarised into a left-associated chain.
    /// </summary>
    /// <remarks>
    /// <c>union { a b c }</c> becomes <c>a b ∪ c ∪</c>. Left association is what keeps a
    /// long chain cheap on the stack: each step merges an accumulated list with one fresh
    /// operand, so the depth stays at 2 however many operands there are, where a balanced
    /// tree of the same size would need log₂(n).
    /// </remarks>
    private SpanBudget EmitOperation(CsgOperation operation, TapeOpcode opcode)
    {
        IReadOnlyList<Solid> operands = operation.Operands;

        // The binder rejects an operator with fewer than two operands, so the loop below
        // always runs at least once.
        SpanBudget budget = Descend(operands[0]);

        for (int i = 1; i < operands.Count; i++)
        {
            SpanBudget right = Descend(operands[i]);
            Emit(opcode, 0);

            budget = SpanBudget.For(opcode, budget, right);
            CheckBudget(operation, opcode, budget);
        }

        return budget;
    }

    /// <summary>
    /// Reports the first subtree whose worst case does not fit the shader's fixed arrays.
    /// </summary>
    /// <remarks>
    /// Only the first, and post-order traversal makes that the innermost one — the subtree
    /// actually at fault. Every enclosing operator would overflow as well, and a cascade of
    /// diagnostics pointing at ancestors would bury the one line worth reading.
    /// </remarks>
    private void CheckBudget(CsgOperation operation, TapeOpcode opcode, SpanBudget budget)
    {
        if (_budgetExceeded)
        {
            return;
        }

        string name = opcode.ToString().ToLowerInvariant();

        if (budget.Spans > GpuLayout.MaxSpans)
        {
            _budgetExceeded = true;
            Report(
                operation,
                $"can produce up to {budget.Spans} spans along a ray; "
                + $"the shader holds {GpuLayout.MaxSpans}",
                name);
        }
        else if (budget.StackDepth > GpuLayout.MaxStackDepth)
        {
            _budgetExceeded = true;
            Report(
                operation,
                $"nests {budget.StackDepth} span lists deep; "
                + $"the shader holds {GpuLayout.MaxStackDepth}",
                name);
        }
    }

    /// <summary>
    /// Reports a budget overflow against the loop that generated the operands, when one did.
    /// </summary>
    /// <remarks>
    /// Loops make an overflowing scene trivial to write for the first time, and the operator
    /// is the wrong thing to point at when they do: <c>union { for (i in 0..500) sphere {…} }</c>
    /// has a <c>union</c> that is exactly right and a count that is not. The loop is the line
    /// to change, so it gets both the position and the number.
    /// </remarks>
    private void Report(CsgOperation operation, string problem, string name)
    {
        LoopOrigin? generator = operation.Operands
            .Select(operand => operand.Generator)
            .FirstOrDefault(origin => origin is not null);

        if (generator is null)
        {
            _diagnostics.Error(operation.Origin, $"this '{name}' {problem}");
            return;
        }

        _diagnostics.Error(
            generator.Span,
            $"this loop over '{generator.Variable}' puts {generator.Count} solids in a "
            + $"'{name}' that {problem}");
    }

    private SpanBudget EmitLeaf(
        Solid solid,
        PrimitiveKind kind,
        Matrix4x4 toWorld,
        float paramA = 0f,
        float paramB = 0f,
        int spans = 1)
    {
        if (!Matrix4x4.Invert(toWorld, out Matrix4x4 toLocal))
        {
            // Emitting nothing leaves an enclosing operator with one operand too few, so
            // the tape is well formed only when compilation reports no error at all. That
            // is the contract SceneCompiler enforces by returning null.
            _diagnostics.Error(
                solid.Origin,
                $"'{solid.Kind.ToLowerInvariant()}' has a transform that cannot be inverted; "
                + "a zero scale collapses the solid to nothing");

            return SpanBudget.None;
        }

        // No overflow check here: GpuLayout.SpansFor clamps every leaf to MaxSpans, so a leaf
        // cannot exceed the budget on its own. What a point-list primitive *can* exceed is the
        // shader array holding its points, and that is checked by its binder, which can point
        // at the field rather than at the whole solid.
        int primitiveIndex = _primitives.Count / GpuLayout.PrimitiveStride;

        _primitives.Add((float)kind);
        _primitives.Add(InternMaterial(_inheritedMaterial ?? Material.Default));
        _primitives.Add(paramA);
        _primitives.Add(paramB);
        AppendRows(toLocal);

        Emit(TapeOpcode.Leaf, primitiveIndex);

        return new SpanBudget(spans, 1);
    }

    /// <summary>
    /// Writes a closed contour as one texel per edge, <c>(a.x, a.y, b.x, b.y)</c>, and
    /// returns the texel index it starts at.
    /// </summary>
    /// <remarks>
    /// Edges rather than points, though it stores each vertex twice. The shader's inner loop
    /// is over edges — it intersects the ray with each one — and having both endpoints in a
    /// single texel keeps that to one fetch instead of two plus a wrap-around test on the
    /// last iteration.
    /// </remarks>
    private int AppendEdges(IReadOnlyList<Vector2> points)
    {
        int offset = _shapes.Count / GpuLayout.ShapeStride;

        for (int i = 0; i < points.Count; i++)
        {
            Vector2 a = points[i];
            Vector2 b = points[(i + 1) % points.Count];

            _shapes.Add(a.X);
            _shapes.Add(a.Y);
            _shapes.Add(b.X);
            _shapes.Add(b.Y);
        }

        return offset;
    }

    /// <summary>
    /// A rotation whose local <c>+Y</c> lands on <paramref name="v"/>, which must be unit
    /// length.
    /// </summary>
    /// <remarks>
    /// The helper vector is picked away from the axis so the cross product stays well
    /// conditioned. A vertical axis is the common case and would be exactly the degenerate
    /// one if the helper were always <c>+Y</c>.
    /// </remarks>
    private static Matrix4x4 UpBasis(Vector3 v)
    {
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

    /// <summary>
    /// The same basis, for a primitive given as two end points rather than as a direction.
    /// </summary>
    private static Matrix4x4 AxisBasis(Vector3 from, Vector3 to, out float height)
    {
        Vector3 axis = to - from;
        height = axis.Length();
        return UpBasis(axis / height);
    }

    private void Emit(TapeOpcode opcode, int operand)
    {
        _tape.Add((int)opcode);
        _tape.Add(operand);
        _tape.Add(0);
        _tape.Add(0);
    }

    /// <summary>
    /// Writes the matrix one row per texel. The shader reassembles it with
    /// <c>mat4(r0, r1, r2, r3)</c>, whose arguments are columns — so feeding it rows
    /// produces the transpose, which is exactly the column-vector form GLSL needs. No
    /// transposition happens on either side.
    /// </summary>
    private void AppendRows(Matrix4x4 matrix)
    {
        _primitives.Add(matrix.M11);
        _primitives.Add(matrix.M12);
        _primitives.Add(matrix.M13);
        _primitives.Add(matrix.M14);

        _primitives.Add(matrix.M21);
        _primitives.Add(matrix.M22);
        _primitives.Add(matrix.M23);
        _primitives.Add(matrix.M24);

        _primitives.Add(matrix.M31);
        _primitives.Add(matrix.M32);
        _primitives.Add(matrix.M33);
        _primitives.Add(matrix.M34);

        _primitives.Add(matrix.M41);
        _primitives.Add(matrix.M42);
        _primitives.Add(matrix.M43);
        _primitives.Add(matrix.M44);
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

        // A metal has no transmission lobe, so the shader would ignore it anyway. Zeroing
        // it here means the "does this scene need transmissive shadow rays" test below can
        // be a plain look at the table.
        float transmission = material.Metallic > 0f ? 0f : material.Transmission;
        _materials.Add(transmission);

        _materials.Add(material.Ior);

        // Same reasoning one step further: a medium only exists inside a solid light can get
        // into, so an opaque material's scattering is zeroed here rather than being left for
        // the shader to ignore. That keeps CompiledScene.HasMedia a plain look at the table.
        _materials.Add(transmission > 0f ? material.Scattering : 0f);
        _materials.Add(material.Anisotropy);
        _materials.Add(0f);

        _materialIndices[material] = index;
        return index;
    }
}
