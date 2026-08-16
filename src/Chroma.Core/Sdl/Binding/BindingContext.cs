using Chroma.Core.Model.Geometry;
using Chroma.Core.Sdl.Source;

namespace Chroma.Core.Sdl.Binding;

/// <summary>
/// Dispatches an evaluated object to its binder and type-checks the result.
/// </summary>
public sealed class BindingContext(NodeBinderRegistry registry, DiagnosticBag diagnostics)
{
    public NodeBinderRegistry Registry { get; } = registry;

    public DiagnosticBag Diagnostics { get; } = diagnostics;

    /// <summary>
    /// The unit the file writes its angles in, from <c>render { angles: … }</c>.
    /// </summary>
    /// <remarks>
    /// Settable because the <c>render</c> block is bound before anything else — see
    /// <see cref="SceneBuilder"/> — so that the mode is known by the time the camera and the
    /// solids that carry a <c>rotate</c> are read, wherever in the file it was written.
    /// </remarks>
    public bool AnglesInRadians { get; set; }

    /// <summary>An angle as the scene model wants it, in degrees.</summary>
    public float ToDegrees(float angle) =>
        AnglesInRadians ? angle * (180f / MathF.PI) : angle;

    /// <summary>
    /// Binds a value that must be an object.
    /// </summary>
    /// <param name="value">The evaluated block.</param>
    /// <param name="defaultTypeName">
    /// Type to assume for an anonymous <c>{ ... }</c> literal, which is what lets
    /// <c>material: { color: ... }</c> stand in for <c>material: material { ... }</c>.
    /// </param>
    public object? Bind(SdlValue value, string? defaultTypeName = null)
    {
        if (value is not ObjectValue block)
        {
            Diagnostics.Error(value.Span, $"expected an object, found {value.Describe()}");
            return null;
        }

        string? typeName = block.TypeName ?? defaultTypeName;

        if (typeName is null)
        {
            Diagnostics.Error(
                block.Span,
                "this object literal needs a type name, for example 'sphere { ... }'");
            return null;
        }

        if (!Registry.TryGet(typeName, out INodeBinder binder))
        {
            Diagnostics.Error(block.TypeNameSpan, $"unknown node type '{typeName}'");
            return null;
        }

        BlockReader reader = new(block, typeName, Diagnostics);
        object? result = binder.Bind(reader, this);

        // Done here rather than in each binder so that no binder can forget it.
        reader.ReportUnusedEntries();

        return result;
    }

    /// <summary>Binds and checks the result is a <typeparamref name="TResult"/>.</summary>
    public TResult? BindAs<TResult>(SdlValue value, string what, string? defaultTypeName = null)
        where TResult : class
    {
        object? bound = Bind(value, defaultTypeName);

        switch (bound)
        {
            case null:
                return null;

            case TResult typed:
                return typed;

            default:
                Diagnostics.Error(value.Span, $"expected {what}, found {value.Describe()}");
                return null;
        }
    }

    public Solid? BindSolid(SdlValue value) => BindAs<Solid>(value, "a solid");
}
