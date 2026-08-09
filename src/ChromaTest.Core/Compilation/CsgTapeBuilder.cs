using System.Numerics;
using ChromaTest.Core.Model.Geometry;
using ChromaTest.Core.Model.Geometry.Operations;
using ChromaTest.Core.Model.Geometry.Primitives;
using ChromaTest.Core.Model.Materials;
using ChromaTest.Core.Sdl.Source;

namespace ChromaTest.Core.Compilation;

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
/// The shader reads no shape parameters at all. Every primitive is evaluated in a
/// canonical form — unit sphere, [-1,1] box, unit cylinder along +Y — and its real
/// dimensions live in the matrix. A non-uniform scale on the canonical sphere therefore
/// gives an ellipsoid for free.
/// </para>
/// </remarks>
internal sealed class CsgTapeBuilder(DiagnosticBag diagnostics) : ISolidVisitor<SpanBudget>
{
    private readonly DiagnosticBag _diagnostics = diagnostics;
    private readonly List<int> _tape = [];
    private readonly List<float> _primitives = [];
    private readonly List<float> _materials = [];
    private readonly Dictionary<Material, int> _materialIndices = [];

    // Accumulated down the tree and restored on the way back up, the same shape as the
    // hierarchy printer's prefix handling.
    private Matrix4x4 _ancestorTransform = Matrix4x4.Identity;
    private Material? _inheritedMaterial;
    private bool _budgetExceeded;

    public IReadOnlyList<int> Tape => _tape;

    public IReadOnlyList<float> Primitives => _primitives;

    public IReadOnlyList<float> Materials => _materials;

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
        Vector3 axis = cylinder.Cap - cylinder.Base;
        float height = axis.Length();
        Vector3 v = axis / height;

        // Pick the helper away from the axis so the cross product stays well conditioned.
        // A vertical cylinder is the common case and would be exactly the degenerate one
        // if the helper were always +Y.
        Vector3 helper = MathF.Abs(v.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX;
        Vector3 u = Vector3.Normalize(Vector3.Cross(helper, v));
        Vector3 w = Vector3.Cross(v, u);

        // Rows are the images of the local axes, so local +Y lands on the cylinder's axis.
        Matrix4x4 basis = new(
            u.X, u.Y, u.Z, 0f,
            v.X, v.Y, v.Z, 0f,
            w.X, w.Y, w.Z, 0f,
            0f, 0f, 0f, 1f);

        return EmitLeaf(
            cylinder,
            PrimitiveKind.Cylinder,
            Matrix4x4.CreateScale(cylinder.Radius, height, cylinder.Radius)
            * basis
            * Matrix4x4.CreateTranslation(cylinder.Base)
            * _ancestorTransform);
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
            _diagnostics.Error(
                operation.Origin,
                $"this '{name}' can produce up to {budget.Spans} spans along a ray; "
                + $"the shader holds {GpuLayout.MaxSpans}");
        }
        else if (budget.StackDepth > GpuLayout.MaxStackDepth)
        {
            _budgetExceeded = true;
            _diagnostics.Error(
                operation.Origin,
                $"this '{name}' nests {budget.StackDepth} span lists deep; "
                + $"the shader holds {GpuLayout.MaxStackDepth}");
        }
    }

    private SpanBudget EmitLeaf(Solid solid, PrimitiveKind kind, Matrix4x4 toWorld)
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

        int primitiveIndex = _primitives.Count / GpuLayout.PrimitiveStride;

        _primitives.Add((float)kind);
        _primitives.Add(InternMaterial(_inheritedMaterial ?? Material.Default));
        _primitives.Add(0f);
        _primitives.Add(0f);
        AppendRows(toLocal);

        Emit(TapeOpcode.Leaf, primitiveIndex);

        return SpanBudget.Leaf;
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

        _materialIndices[material] = index;
        return index;
    }
}
