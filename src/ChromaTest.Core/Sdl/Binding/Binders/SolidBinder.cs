using System.Numerics;
using ChromaTest.Core.Model.Geometry;
using ChromaTest.Core.Model.Materials;

namespace ChromaTest.Core.Sdl.Binding.Binders;

/// <summary>
/// Base for every solid binder. Handles the modifiers that all solids share — primitives
/// and CSG operations alike — so a new shape only has to describe its own geometry.
/// </summary>
public abstract class SolidBinder : INodeBinder
{
    public abstract string Name { get; }

    public object? Bind(BlockReader reader, BindingContext context)
    {
        Solid? solid = BindShape(reader, context);
        if (solid is null)
        {
            // Still read the shared modifiers so their own mistakes get reported, and so
            // they are not then flagged a second time as unknown fields.
            ReadMaterial(reader, context);
            ReadTransform(reader);
            return null;
        }

        solid.Material = ReadMaterial(reader, context);
        solid.Transform = ReadTransform(reader);
        solid.Origin = reader.NameSpan;
        return solid;
    }

    /// <summary>The geometry proper. Returns null when it could not be read.</summary>
    protected abstract Solid? BindShape(BlockReader reader, BindingContext context);

    private static Material? ReadMaterial(BlockReader reader, BindingContext context)
    {
        BoundField? field = reader.Field("material");
        return field is null
            ? null
            : context.BindAs<Material>(field.Value, "a material", defaultTypeName: "material");
    }

    /// <summary>
    /// Transform modifiers, in the order they appear in the block. Order is semantic:
    /// translating then rotating does not produce the same solid as the reverse, so this
    /// walks the entries rather than looking each name up independently.
    /// </summary>
    private static Transform ReadTransform(BlockReader reader)
    {
        List<TransformStep> steps = [];

        for (int i = 0; i < reader.Entries.Count; i++)
        {
            if (reader.Entries[i] is not BoundField field)
            {
                continue;
            }

            TransformKind? kind = field.Name switch
            {
                "translate" => TransformKind.Translate,
                "rotate" => TransformKind.Rotate,
                "scale" => TransformKind.Scale,
                _ => null,
            };

            if (kind is null)
            {
                continue;
            }

            reader.MarkUsed(i);

            bool isScale = kind == TransformKind.Scale;
            Vector3 fallback = isScale ? Vector3.One : Vector3.Zero;
            Vector3 value = ToVector(reader, field, fallback, allowScalar: isScale);

            steps.Add(new TransformStep(kind.Value, value));
        }

        return steps.Count == 0 ? Transform.Identity : new Transform(steps);
    }

    private static Vector3 ToVector(
        BlockReader reader,
        BoundField field,
        Vector3 fallback,
        bool allowScalar)
    {
        if (allowScalar && field.Value is NumberValue scalar)
        {
            float component = (float)scalar.Value;
            return new Vector3(component, component, component);
        }

        if (field.Value is VectorValue vector && vector.Components.Count == 3)
        {
            return new Vector3(
                (float)vector.Components[0],
                (float)vector.Components[1],
                (float)vector.Components[2]);
        }

        string expected = allowScalar
            ? "a vector of 3 components or a number"
            : "a vector of 3 components";

        reader.Diagnostics.Error(
            field.Value.Span,
            $"field '{field.Name}' expects {expected}, found {field.Value.Describe()}");

        return fallback;
    }
}
