using ChromaTest.Core.Sdl.Binding.Binders;

namespace ChromaTest.Core.Sdl.Binding;

/// <summary>
/// Maps a node name to the binder that understands it.
/// </summary>
public sealed class NodeBinderRegistry
{
    private readonly Dictionary<string, INodeBinder> _binders = new(StringComparer.Ordinal);

    /// <summary>Every node type the language currently defines.</summary>
    public static NodeBinderRegistry CreateDefault()
    {
        NodeBinderRegistry registry = new();

        registry.Register(new CameraBinder());
        registry.Register(new RenderBinder());
        registry.Register(new PointLightBinder());
        registry.Register(new DirectionalLightBinder());
        registry.Register(new MaterialBinder());

        registry.Register(new SphereBinder());
        registry.Register(new BoxBinder());
        registry.Register(new CylinderBinder());
        registry.Register(new ConeBinder());
        registry.Register(new PlaneBinder());
        registry.Register(new TorusBinder());
        registry.Register(new PrismBinder());
        registry.Register(new LatheBinder());
        registry.Register(new BlobBinder());
        registry.Register(new BlobSphereBinder());

        registry.Register(new UnionBinder());
        registry.Register(new IntersectionBinder());
        registry.Register(new DifferenceBinder());

        return registry;
    }

    public void Register(INodeBinder binder) => _binders[binder.Name] = binder;

    public bool TryGet(string name, out INodeBinder binder) => _binders.TryGetValue(name, out binder!);
}
