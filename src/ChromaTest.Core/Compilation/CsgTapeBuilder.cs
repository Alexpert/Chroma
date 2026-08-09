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

    public SpanBudget VisitUnion(Union union) => ReportNotRenderedYet(union);

    public SpanBudget VisitIntersection(Intersection intersection) => ReportNotRenderedYet(intersection);

    public SpanBudget VisitDifference(Difference difference) => ReportNotRenderedYet(difference);

    private SpanBudget ReportNotRenderedYet(CsgOperation operation)
    {
        // Refusing outright beats emitting the leaves and quietly drawing their union:
        // a picture that is wrong in a way the file does not explain costs far more to
        // diagnose than a message saying the feature is not there.
        _diagnostics.Error(
            operation.Origin,
            $"'{operation.Kind.ToLowerInvariant()}' cannot be rendered yet; "
            + "CSG operators arrive in the next iteration");

        return SpanBudget.None;
    }

    private SpanBudget EmitLeaf(Solid solid, PrimitiveKind kind, Matrix4x4 toWorld)
    {
        if (!Matrix4x4.Invert(toWorld, out Matrix4x4 toLocal))
        {
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

        _tape.Add((int)TapeOpcode.Leaf);
        _tape.Add(primitiveIndex);
        _tape.Add(0);
        _tape.Add(0);

        return SpanBudget.Leaf;
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
        _materials.Add(material.Specular);

        _materials.Add(material.Shininess);
        _materials.Add(material.Reflectivity);
        _materials.Add(0f);
        _materials.Add(0f);

        _materialIndices[material] = index;
        return index;
    }
}
