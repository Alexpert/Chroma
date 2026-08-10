namespace Chroma.Core.Sdl.Binding;

/// <summary>
/// Turns one kind of block into one scene object. This is the extension point of the
/// language: adding a node type is a new binder plus one registration, with no change to
/// the lexer, the parser or any existing binder.
/// </summary>
/// <remarks>
/// The return type is <see cref="object"/> because binders produce unrelated things — a
/// camera, a light, a solid, a material. <see cref="BindingContext.BindAs{TResult}"/>
/// checks the type at the point of use and reports a readable mismatch.
/// </remarks>
public interface INodeBinder
{
    /// <summary>The keyword this binder handles, exactly as written in a scene file.</summary>
    string Name { get; }

    /// <summary>Returns null when the block could not be bound, having reported why.</summary>
    object? Bind(BlockReader reader, BindingContext context);
}
