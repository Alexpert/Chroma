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
    /// <summary>A subtree's result: the variable holding its spans, how many it can hold, and where it is.</summary>
    /// <param name="Slot">
    /// Which pool slot <paramref name="Variable"/> names, or -1 for something the pool does not
    /// own. Carried so the node can be handed back when whatever consumes it has read it.
    /// </param>
    internal readonly record struct Node(string Variable, int Spans, Aabb Bounds, int Slot = -1);

    /// <summary>The canonical box, sphere and every other centred primitive: [-1, 1] on each axis.</summary>
    private static readonly Aabb CanonicalCube = new(new Vector3(-1f), new Vector3(1f));

    /// <summary>The canonical cylinder and cone: unit radius about +Y, from y = 0 to y = 1.</summary>
    private static readonly Aabb CanonicalColumn = new(new Vector3(-1f, 0f, -1f), new Vector3(1f, 1f, 1f));

    /// <summary>One emitted shape: its body, what it answers in, where it stands, what it weighs.</summary>
    private sealed record Emitted(Node Result, SpanRef Answer, ShapeGroup Group, int Cost);

    /// <summary>What one root turned out to be: how it emits, what it weighs, where it starts.</summary>
    /// <param name="Key">
    /// The GLSL it emitted, which is what two roots have to agree on to be one shape, or null for
    /// a root the emitter refused.
    /// </param>
    /// <param name="Cost">What the shape weighs, in the units of <see cref="ShapeCost"/>.</param>
    /// <param name="FirstLeaf">
    /// Where the shape's first leaf ends up, which is what the caller normalises against so that
    /// two appearances written with their positions inside their primitives, as <c>center:</c>,
    /// <c>min:</c>/<c>max:</c>, <c>base:</c>/<c>cap:</c> do, come out identical.
    /// </param>
    /// <param name="Spans">
    /// How wide the shape's own span list is at worst, which is how much state a thread carries
    /// while resolving it. Reported here for the same reason <paramref name="Cost"/> is: it is
    /// what <see cref="Compilation.RootSplitter"/> decides on, and taking it from the walk that
    /// will emit the shape means the number decided on is the number produced.
    /// </param>
    /// <param name="Bounds">
    /// The box the shape occupies, in the space it was probed in. Used to find out whether two
    /// operands of one <c>union</c> overlap, which is the whole of whether cutting between them
    /// is sound. See <see cref="Compilation.RootSplitter"/>.
    /// </param>
    internal readonly record struct Probed(
        string? Key, int Cost, Matrix4x4 FirstLeaf, int Spans, Aabb Bounds);

    private readonly SpanLibrary _spans = new();
    private readonly GlslWriter _leaves = new();
    private readonly GlslWriter _shapes = new();

    /// <summary>
    /// The tables, which belong to the whole scene rather than to this chunk of it.
    /// </summary>
    /// <remarks>
    /// The five fields below are <b>aliases</b> into it, so that every site that appends to a
    /// table reads as it did before chunking existed. Two emitters handed the same
    /// <see cref="SceneTables"/> append to the same lists, which is what keeps a leaf's index
    /// scene-wide — and a leaf's index is a literal in the generated GLSL.
    /// </remarks>
    private readonly SceneTables _tables;

    private readonly List<float> _primitives;
    private readonly List<float> _materials;
    private readonly List<float> _shapeData;
    private readonly Dictionary<Material, int> _materialIndices;

    /// <summary>Where a run of materials belonging to one appearance starts, keyed on the run.</summary>
    /// <remarks>
    /// A shared shape's leaves name a slot, and the instance record names where slot zero is, so
    /// an appearance's materials have to be <b>contiguous</b>. Two appearances wearing the same
    /// materials in the same order share one run: sixteen ivory pawns cost one entry, not
    /// sixteen.
    /// </remarks>
    private readonly Dictionary<string, int> _materialRuns;

    private readonly List<Emitted> _emitted = [];

    /// <summary>Which emitted shape each leaf came out of, parallel to the primitive table.</summary>
    private readonly List<int> _leafShapes = [];

    /// <summary>How many slots of each width the widest shape needed, i.e. what to declare.</summary>
    private readonly Dictionary<int, int> _poolSize = [];

    /// <summary>Slots of each width handed back by the shape being walked, newest first.</summary>
    private readonly Dictionary<int, Stack<int>> _poolFree = [];

    /// <summary>Slots of each width the shape being walked has taken so far.</summary>
    private readonly Dictionary<int, int> _poolTaken = [];

    private GlslWriter _body = new();
    private Matrix4x4 _ancestorTransform = Matrix4x4.Identity;
    private bool _failed;

    /// <summary>
    /// What the bodies called by the shape being walked weigh, which the driver will inline into
    /// it. Its own statements are in <see cref="_body"/>; this is everything they reach.
    /// </summary>
    private int _inlined;

    /// <summary>What the shape just walked weighs. See <see cref="ShapeCost"/>.</summary>
    private int _cost;

    /// <summary>
    /// Which material slot each leaf of the shape being emitted wears, and how far through it we
    /// are.
    /// </summary>
    /// <remarks>
    /// Resolved by <see cref="ShapeCanonicalizer"/> rather than here. The emitter used to carry
    /// an <c>_inheritedMaterial</c> down the tree and work it out itself, which meant the rule
    /// for what a leaf is made of existed twice, once in the thing that decides two shapes are
    /// the same, once in the thing that emits them, and two copies of that rule disagreeing is a
    /// scene rendered in the wrong colours with nothing to point at.
    /// </remarks>
    private IReadOnlyList<int> _leafSlots = [];

    private int _leafOrdinal;

    /// <summary>
    /// What a leaf writes into its material field, per slot: an absolute index for a singleton,
    /// the slot itself for a shared shape.
    /// </summary>
    /// <remarks>
    /// A singleton has exactly one appearance, so its materials are known at emission and are
    /// folded in as they always were. A shared body cannot fold anything, since it serves an ivory
    /// pawn and an obsidian one, so it records the slot and the instance record supplies the
    /// base to add to it.
    /// </remarks>
    private int[]? _slotIndices;

    /// <summary>Where the first leaf of the shape being walked ends up. See <see cref="Probe"/>.</summary>
    private Matrix4x4 _firstLeaf = Matrix4x4.Identity;

    /// <summary>Texel index the shape data of the leaf being emitted starts at.</summary>
    /// <remarks>
    /// The shape buffer is written by the visit method before it calls <see cref="EmitLeaf"/>,
    /// so this is captured on the way in and read on the way out.
    /// </remarks>
    private int _shapeOffset;

    private readonly LeafEmitter _leafEmitter;
    private readonly DiagnosticBag _diagnostics;

    /// <param name="tables">
    /// The scene's tables, shared with every other chunk of it. A fresh set when omitted, which
    /// is what a probe wants and what a scene of one chunk gets.
    /// </param>
    /// <param name="sharedIdBase">
    /// The number this chunk's first shared shape answers to in the generated <c>switch</c>.
    /// Shape ids are scene-wide because the instance records that carry them are, so each chunk
    /// starts where the last one stopped and its own switch is sparse rather than dense — which
    /// costs nothing, since a switch over constants is a jump table either way.
    /// </param>
    /// <param name="nodeBase">
    /// Where this chunk's BVH begins in the scene's node table. Written into the walk as a
    /// literal rather than arriving as a uniform, so a scene of one chunk emits exactly the text
    /// it emitted before chunking existed. Node counts stay a uniform, for the opposite reason:
    /// a literal one would let the driver unroll the walk, which is the whole cost instancing
    /// exists to avoid.
    /// </param>
    public GeometryEmitter(
        DiagnosticBag diagnostics,
        SceneTables? tables = null,
        int sharedIdBase = 0,
        int nodeBase = 0)
    {
        _diagnostics = diagnostics;
        _leafEmitter = new LeafEmitter(_spans);

        _tables = tables ?? new SceneTables();
        _primitives = _tables.Primitives;
        _materials = _tables.Materials;
        _shapeData = _tables.Shapes;
        _materialIndices = _tables.MaterialIndices;
        _materialRuns = _tables.MaterialRuns;

        _sharedIdBase = sharedIdBase;
        _nodeBase = nodeBase;
    }

    private readonly int _sharedIdBase;
    private readonly int _nodeBase;

    public IReadOnlyList<float> Primitives => _primitives;

    public IReadOnlyList<float> Materials => _materials;

    public IReadOnlyList<float> Shapes => _shapeData;

    public int LeafCount => _tables.LeafCount;

    /// <summary>How many shared shapes this chunk emitted, so the next can be numbered after it.</summary>
    public int SharedCount => _emitted.Count(shape => shape.Group.Instanced);

    public int ShapeCount => _emitted.Count;

    /// <summary>Widest span list any shape produces, for the console line only.</summary>
    public int WidestRoot => _emitted.Count == 0 ? 0 : _emitted.Max(shape => shape.Result.Spans);

    /// <summary>What one appearance of each distinct shape turned out to weigh.</summary>
    /// <remarks>
    /// The same number <see cref="Probe"/> reported to the partition, arrived at by the walk that
    /// really emitted the shape rather than by the one that guessed at it. That the two agree is
    /// the seam worth a test: the partition decides what to share on the probe's number, and a
    /// probe that quietly costed something differently would make every decision on a scene that
    /// is not the scene being built.
    /// </remarks>
    public IReadOnlyDictionary<ShapeGroup, int> ShapeCosts
    {
        get
        {
            Dictionary<ShapeGroup, int> costs = [];

            foreach (Emitted shape in _emitted)
            {
                costs[shape.Group] = shape.Cost;
            }

            return costs;
        }
    }

    /// <summary>What the whole scene weighs, counting a folded shape once per placement.</summary>
    public int TotalCost => _emitted.Sum(shape => shape.Cost);

    /// <summary>Emits one distinct shape as its own function, to be resolved on its own.</summary>
    /// <remarks>
    /// <para>
    /// Roots are implicitly unioned but are resolved one at a time rather than merged, exactly
    /// as the tape did. Merging would make a scene of nine separate spheres cost a nine-span
    /// list, where nine independent roots each cost one.
    /// </para>
    /// <para>
    /// A shape is emitted once however many times it appears. The two cases differ in one thing
    /// and it is the frame the body is written in. A <b>singleton</b> is emitted in world space,
    /// with its placement folded into its leaves as it always was, and reaches the ray through
    /// its own guarded block. A <b>shared</b> shape is emitted in its own space, and its
    /// placements arrive from a buffer, which is what stops the driver from being handed the
    /// same pawn sixteen times.
    /// </para>
    /// </remarks>
    public void EmitShape(ShapeGroup group)
    {
        if (group.Instanced)
        {
            // One body for every appearance, walked from the origin of its own space, reached
            // through the buffer. The group's tree serves them all, which is exactly what the
            // signature guarantees: every member emits the same GLSL from the shape frame.
            EmitBody(group, group.Root, group.ShapeFrame, null, $"{group.Placements.Count} instances");
            return;
        }

        // Not worth the indirection, so every appearance is written out where it stands: from its
        // OWN tree and its own spine, letting its leaves fold in whatever position they carry
        // themselves, and wearing an absolute material index. This is byte-for-byte what every
        // root was before instancing existed.
        //
        // Its own tree, and not the group's, because the group's is whichever root reached it
        // first and the position that tells the others apart was normalised out of it. Emitting
        // the group's tree here draws every appearance on top of the first, which is a ceiling
        // that disappears rather than a scene that runs slowly.
        foreach (ShapePlacement placement in group.Placements)
        {
            EmitBody(
                group,
                placement.Root,
                placement.Spine,
                AbsoluteSlots(placement.Materials, group.LeafSlots),
                "folded in");
        }
    }

    private void EmitBody(
        ShapeGroup group,
        Solid root,
        Matrix4x4 ancestor,
        int[]? slotIndices,
        string note)
    {
        int index = _emitted.Count;

        Node node = Emit(root, ancestor, group.LeafSlots, slotIndices, index, note);

        if (_failed)
        {
            return;
        }

        // Asked for here rather than in WriteTraceScene: the library is written out before the
        // trace function is, so anything the trace function needs has to be registered while
        // the shapes are still being walked.
        _spans.Resolve(new SpanRef(node.Spans, node.Variable));
        _spans.Occludes(new SpanRef(node.Spans, node.Variable));

        _emitted.Add(new Emitted(
            node with { Variable = $"shape{index}" },
            new SpanRef(node.Spans, node.Variable),
            group,
            _cost));
    }

    /// <summary>
    /// Emits one shape into a throwaway emitter and returns exactly what it produced.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is how <see cref="ShapeCanonicalizer"/> decides two roots are the same shape, and it
    /// is the one comparison that cannot be wrong: what a shape <i>is</i> is defined as what it
    /// emits, so there is no second description of a solid to drift out of step with the first.
    /// It is <c>LeafEmitter.KeyOf</c>'s discipline of comparing the literals a solid will become
    /// rather than the floats it was written with, widened from a leaf to a tree.
    /// </para>
    /// <para>
    /// Emitting every root twice to find this out costs milliseconds on the largest scene in the
    /// repository, against a comparison written by hand that would have to know how ten
    /// primitives fold their parameters into a matrix and would be wrong the first time an
    /// eleventh was added.
    /// </para>
    /// <para>
    /// It answers what a shape <i>weighs</i> for the same reason and at the same time. The
    /// partition has to know before anything is emitted for real, since the cost is what it
    /// decides on, and taking the number from the same walk that produces the signature means the
    /// number it decides on is by construction the number the emitter will produce.
    /// </para>
    /// </remarks>
    internal static Probed Probe(Solid shapeRoot, Matrix4x4 ancestor, IReadOnlyList<int> leafSlots)
    {
        GeometryEmitter probe = new(new DiagnosticBag(new SourceText("<probe>", string.Empty)));

        Node node = probe.Emit(shapeRoot, ancestor, leafSlots, null, 0, "probe");

        // A shape the emitter refuses has no signature and cannot be shared. The real emission
        // reports why, pointing at the solid that carries the bad transform.
        return new Probed(
            probe._failed ? null : probe.Signature(),
            probe._cost,
            probe._firstLeaf,
            node.Spans,
            node.Bounds);
    }

    /// <summary>Walks one shape and writes its body, without deciding what it is for.</summary>
    private Node Emit(
        Solid shapeRoot,
        Matrix4x4 ancestor,
        IReadOnlyList<int> leafSlots,
        int[]? slotIndices,
        int index,
        string note)
    {
        _body = new GlslWriter();

        // Shapes run one at a time, so each starts with the whole pool free. What survives across
        // them is only the high-water mark, which is what gets declared.
        _poolFree.Clear();
        _poolTaken.Clear();

        _leafSlots = leafSlots;
        _leafOrdinal = 0;
        _slotIndices = slotIndices;
        _ancestorTransform = ancestor;
        _inlined = 0;

        Node node = shapeRoot.Accept(this);

        // Its own statements, plus every body the driver will inline into them, plus the pair of
        // functions every shape is resolved through.
        _cost = _body.Cost + _inlined + SpanLibrary.RootCost + ShapeCost.Reach;

        if (_failed)
        {
            return node;
        }

        _shapes.Line(
            $"// shape {index}, {node.Spans} span{(node.Spans == 1 ? "" : "s")} at worst, "
            + $"answered in {node.Variable} -- {note}");
        _shapes.Open($"void shape{index}(vec3 ro, vec3 rd)");

        foreach (string line in _body.ToString().TrimEnd('\n').Split('\n'))
        {
            _shapes.Line(line);
        }

        _shapes.Close();
        _shapes.Line();

        return node;
    }

    /// <summary>Everything a probe emitted, as the text and the table bytes it would upload.</summary>
    /// <remarks>
    /// <para>
    /// The source alone is not the whole shape: <c>paramB</c>, a lathe's smooth flag and the
    /// offset into the shape buffer reach the GPU through the primitive record rather than
    /// through the generated code.
    /// </para>
    /// <para>
    /// Nothing is masked out, and in particular not the material field. A probe is emitted with
    /// no slot table, so that field holds the material <b>slot</b> rather than an index into the
    /// material table, and the slot pattern is structural. Two solids of the same geometry
    /// wearing one material between them and two wearing one each are not the same shape, and
    /// treating them as one hands the second appearance a slot list longer than its run of
    /// materials.
    /// </para>
    /// </remarks>
    private string Signature()
    {
        GlslWriter w = new();

        _leafEmitter.WriteBodies(w);
        Paste(w, _leaves);
        Paste(w, _shapes);

        foreach (float value in _primitives)
        {
            w.Line(GlslWriter.Float(value));
        }

        return w.ToString();
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
        WritePool(w);
        _leafEmitter.WriteHelpers(w);

        _leafEmitter.WriteBodies(w);

        w.Line("// --- Leaves ----------------------------------------------------------------------");
        w.Line();
        Paste(w, _leaves);

        _spans.WriteOperators(w);

        w.Line("// --- Roots -----------------------------------------------------------------------");
        w.Line();
        Paste(w, _shapes);

        WriteTraceScene(w);
        return w.ToString();
    }

    /// <summary>
    /// Which shape each leaf belongs to, as the number an instance record carries, or -1 when the
    /// leaf's own matrix already reaches the world.
    /// </summary>
    public int[] LeafShapes
    {
        get
        {
            int[] ids = SharedIds();
            return [.. _leafShapes.Select(shape => ids[shape])];
        }
    }

    /// <summary>
    /// The number each emitted shape answers to in the generated switch, or -1 for one that is
    /// reached by name instead.
    /// </summary>
    /// <remarks>
    /// Numbered across shared shapes only, so a folded shape costs no case label, and continuing
    /// from <see cref="_sharedIdBase"/> rather than from zero so the numbering is scene-wide.
    /// It has to be: the id travels in the instance record, and the instance table is one table
    /// for the whole scene however many chunks read it.
    /// </remarks>
    private int[] SharedIds()
    {
        int[] ids = new int[_emitted.Count];
        int next = _sharedIdBase;

        for (int shape = 0; shape < _emitted.Count; shape++)
        {
            ids[shape] = _emitted[shape].Group.Instanced ? next++ : -1;
        }

        return ids;
    }

    /// <summary>
    /// Packs every appearance of every shared shape, and the tree that finds them.
    /// </summary>
    /// <remarks>
    /// Called after every shape has been emitted, because an instance's world box is its shape's
    /// box put where the instance stands, and a shape's box is not known until it is walked.
    /// <para>
    /// One tree per chunk rather than one for the scene, because a chunk's walk may only reach
    /// the shapes that chunk emitted a body for. The records themselves go into one scene-wide
    /// table, so <paramref name="instanceBase"/> says where this chunk's begin and the nodes
    /// store the scene-wide index — which is what <c>Hit.instance</c> then means, whichever chunk
    /// produced the hit.
    /// </para>
    /// </remarks>
    public (float[] Instances, float[] Nodes) PackInstances(int instanceBase = 0)
    {
        List<Aabb> boxes = [];
        List<(int Shape, ShapePlacement Placement)> appearances = [];
        int[] ids = SharedIds();

        for (int shape = 0; shape < _emitted.Count; shape++)
        {
            if (ids[shape] < 0)
            {
                continue;
            }

            foreach (ShapePlacement placement in _emitted[shape].Group.Placements)
            {
                appearances.Add((ids[shape], placement));
                boxes.Add(_emitted[shape].Result.Bounds.Transformed(placement.Placement));
            }
        }

        if (appearances.Count == 0)
        {
            return ([], []);
        }

        Bvh.Result bvh = Bvh.Build(boxes);

        List<float> instances = new(appearances.Count * GpuLayout.InstanceStride);

        foreach (int source in bvh.Order)
        {
            (int shape, ShapePlacement placement) = appearances[source];

            // ShapeCanonicalizer only shares a placement whose determinant is positive, so this
            // cannot fail. A singular transform is caught at the leaf that carries it, with a
            // diagnostic pointing at the solid rather than at the instance table.
            Matrix4x4.Invert(placement.Placement, out Matrix4x4 toShape);

            instances.Add(shape);
            instances.Add(InternMaterials(placement.Materials));
            instances.Add(0f);
            instances.Add(0f);
            AppendRows(instances, toShape);
        }

        List<float> nodes = new(bvh.Nodes.Count * GpuLayout.NodeStride);

        foreach (Bvh.Node node in bvh.Nodes)
        {
            nodes.Add(node.Bounds.Min.X);
            nodes.Add(node.Bounds.Min.Y);
            nodes.Add(node.Bounds.Min.Z);
            nodes.Add(node.Escape);

            nodes.Add(node.Bounds.Max.X);
            nodes.Add(node.Bounds.Max.Y);
            nodes.Add(node.Bounds.Max.Z);

            // Scene-wide, so that the shading half can compose a placement from Hit.instance
            // without knowing which chunk found it. An interior node stores -1 and stays -1.
            nodes.Add(node.Item < 0 ? node.Item : instanceBase + node.Item);
        }

        return ([.. instances], [.. nodes]);
    }

    /// <summary>Takes a span list of the given width out of the pool.</summary>
    /// <remarks>
    /// <para>
    /// Every span list in the scene is a <b>file-scope global</b> drawn from a pool, not a local
    /// declared where the node that produces it is emitted. That reads worse and is the only way
    /// a real scene links.
    /// </para>
    /// <para>
    /// The driver inlines every root into <c>traceScene</c> and then allocates storage per
    /// <b>variable</b>, not per live range: with a local per node, a chess set's hundred roots
    /// asked for some two thousand spans of arrays at once and the program died with
    /// <c>error C5041: cannot locate suitable resource to bind variable</c>. Pooling asks instead
    /// for what is live <b>simultaneously</b>, which is a property of the deepest single root
    /// rather than of the scene: the same chess set needs about three hundred, and adding a
    /// hundred more pieces adds none.
    /// </para>
    /// <para>
    /// A slot is taken before the operands it is built from are released, so an operator's result
    /// can never alias its own inputs.
    /// </para>
    /// </remarks>
    private Node Take(int width, Aabb bounds)
    {
        _spans.Type(width);

        if (_poolFree.TryGetValue(width, out Stack<int>? free) && free.Count > 0)
        {
            int reused = free.Pop();
            return new Node($"s{width}_{reused}", width, bounds, reused);
        }

        int slot = _poolTaken.GetValueOrDefault(width);
        _poolTaken[width] = slot + 1;

        if (slot + 1 > _poolSize.GetValueOrDefault(width))
        {
            _poolSize[width] = slot + 1;
        }

        return new Node($"s{width}_{slot}", width, bounds, slot);
    }

    /// <summary>What the span library needs of a node: its width and the global holding it.</summary>
    private static SpanRef Ref(Node node) => new(node.Spans, node.Variable);

    /// <summary>Hands a slot back, once whatever consumes it has been emitted.</summary>
    private void Release(Node node)
    {
        if (node.Slot < 0)
        {
            return;
        }

        if (!_poolFree.TryGetValue(node.Spans, out Stack<int>? free))
        {
            free = new Stack<int>();
            _poolFree[node.Spans] = free;
        }

        free.Push(node.Slot);
    }

    private void WritePool(GlslWriter w)
    {
        w.Line("// --- The span-list pool ------------------------------------------------------------");
        w.Line("// Scratch for every root, sized to how many lists of each width are live at once in the");
        w.Line("// deepest single root -- not to how many nodes the scene has. See Codegen/GeometryEmitter.");
        w.Line();

        foreach ((int width, int count) in _poolSize.OrderBy(entry => entry.Key))
        {
            for (int slot = 0; slot < count; slot++)
            {
                w.Line($"SpanList_{width} s{width}_{slot};");
            }
        }

        w.Line();
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
    /// The whole scene for one ray: the singletons in turn, then the tree over everything that
    /// repeats.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A singleton's guard is a plain <c>if</c> on a constant box rather than the tape's jump
    /// instruction, so it costs nothing to have and nothing to skip. A shape with a
    /// <c>plane</c> in it is unbounded and gets no guard at all rather than one that always
    /// answers yes.
    /// </para>
    /// <para>
    /// The loop below it is the whole point of the work, and it rests on one property: the
    /// driver expands a loop <b>once</b> when it cannot know the trip count. A scene of thirty-two
    /// pieces used to be thirty-two copies of a piece in the assembly, because the loop over
    /// roots was written out at compile time; bounding it on <c>uNodeCount</c> instead makes the
    /// program the size of the six distinct pieces, whatever the board holds. The price is that
    /// a placement is four fetches rather than a folded literal, and it is paid only by shapes
    /// that repeat. See documents/gpu-backends.md.
    /// </para>
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
        w.Line("// The ray's own share of every tolerance below, set once for the whole walk. See the");
        w.Line("// remarks on gTScale in raytrace.glsl: a tolerance on t cannot be relative to t alone,");
        w.Line("// because at t near zero what the point carries is the rounding of the ORIGIN.");
        w.Line("gTScale = max(max(abs(ro.x), abs(ro.y)), abs(ro.z)) / max(length(rd), TINY);");
        w.Line();

        foreach (Emitted shape in _emitted.Where(shape => !shape.Group.Instanced))
        {
            Aabb bounds = shape.Result.Bounds;
            bool guarded = bounds.IsFinite && !bounds.IsEmpty;

            if (guarded)
            {
                w.Open(
                    $"if (boundHit(ro, rd, {GlslWriter.Vec3(bounds.Min)}, "
                    + $"{GlslWriter.Vec3(bounds.Max)}, min(maxT, best.t)))");
            }
            else
            {
                w.Open("");
            }

            w.Line($"{shape.Result.Variable}(ro, rd);");
            w.Line();
            WriteFold(w, shape.Answer, "-1");
            w.Close();
        }

        WriteInstanceWalk(w);

        w.Line();
        w.Line("return best;");
        w.Close();
    }

    /// <summary>
    /// What to do with a finished span list: give up early if the question was only whether
    /// anything is in the way, otherwise fold it into the running nearest hit.
    /// </summary>
    /// <param name="instance">
    /// Which appearance produced the list, or <c>-1</c> for a singleton, whose leaves already
    /// carry world matrices and absolute material indices, so there is nothing for the shading
    /// path to compose.
    /// </param>
    private void WriteFold(GlslWriter w, SpanRef answer, string instance)
    {
        w.Open("if (anyHit)");
        w.Open($"if ({_spans.Occludes(answer)}(maxT))");
        w.Line("best.found = true;");
        w.Line("return best;");
        w.Close();
        w.Close();
        w.Open("else");
        w.Line($"{_spans.Resolve(answer)}(best, {instance});");
        w.Close();
    }

    /// <summary>The BVH walk over every appearance of every shared shape.</summary>
    /// <remarks>
    /// Stackless, by escape index: descending is <c>++node</c> and skipping a subtree is a jump
    /// to what the node stores. A traversal stack would be an array declared inside
    /// <c>traceScene</c>, and the driver allocates storage per variable rather than per live
    /// range, so it would be one stack per inlined call site of the whole scene walk, which is
    /// the shape of the failure recorded in documents/gpu-backends.md as <c>error C5041</c>.
    /// </remarks>
    private void WriteInstanceWalk(GlslWriter w)
    {
        List<Emitted> shared = [.. _emitted.Where(shape => shape.Group.Instanced)];

        if (shared.Count == 0)
        {
            return;
        }

        w.Line();
        w.Line($"// {shared.Count} shared shape{(shared.Count == 1 ? "" : "s")}, "
            + $"{shared.Sum(shape => shape.Group.Placements.Count)} placements between them.");
        w.Line("// uNodeCount is a uniform so that this loop is compiled once rather than once per");
        w.Line("// placement -- which is the difference between a scene that links and one that does not.");
        // Where this chunk's tree sits in the scene's node table, as a literal. Zero for a scene
        // the megakernel can take, and then this reads exactly as it did before chunking existed.
        // Escape indices stay tree-local, so only the fetch moves.
        string at = _nodeBase == 0 ? "node" : $"({_nodeBase} + node)";

        w.Line("int node = 0;");
        w.Line();
        w.Open("while (node < uNodeCount)");
        w.Line($"vec4 lo = NODE({at} * NODE_TEXELS);");
        w.Line($"vec4 hi = NODE({at} * NODE_TEXELS + 1);");
        w.Line();
        w.Open("if (!boundHit(ro, rd, lo.xyz, hi.xyz, min(maxT, best.t)))");
        w.Line("node = int(lo.w);   // missed: skip the whole subtree");
        w.Line("continue;");
        w.Close();
        w.Line();
        w.Line("int instance = int(hi.w);");
        w.Line();
        w.Open("if (instance < 0)");
        w.Line("++node;             // an interior node: descend");
        w.Line("continue;");
        w.Close();
        w.Line();
        w.Line("int slot = instance * INSTANCE_TEXELS;");
        w.Line();
        w.Line("// Neither transform renormalises the direction. t is measured in world units and has");
        w.Line("// to stay that way for the nearest-hit comparison above to mean anything -- the same");
        w.Line("// reason a leaf's local direction is built with w = 0 and left alone.");
        w.Line("mat4 toShape = fetchInstanceMatrix(slot);");
        w.Line("vec3 ros = (toShape * vec4(ro, 1.0)).xyz;");
        w.Line("vec3 rds = (toShape * vec4(rd, 0.0)).xyz;");
        w.Line();
        w.Open("switch (int(INSTANCE(slot).x))");

        for (int i = 0; i < shared.Count; i++)
        {
            Emitted shape = shared[i];

            // The scene-wide id, which for the first chunk is i and for the rest is not. Its own
            // cases are the only ones it needs: a node in this chunk's tree can only name an
            // instance of a shape in this chunk.
            w.Open($"case {_sharedIdBase + i}:");
            w.Line($"{shape.Result.Variable}(ros, rds);");
            WriteFold(w, shape.Answer, "instance");
            w.Line("break;");
            w.Close();
        }

        w.Line("default: break;");
        w.Close();
        w.Line();
        w.Line("++node;");
        w.Close();
    }

    /// <summary>Walks one subtree, carrying the ancestors' transform down it.</summary>
    /// <remarks>
    /// Materials no longer travel with it. Which material a leaf ends up wearing is settled by
    /// <see cref="ShapeCanonicalizer"/>, because it has to be: two roots are the same shape when
    /// their leaves wear materials in the same <i>pattern</i>, and that question cannot be
    /// answered by a walk that resolves the pattern to values as it goes.
    /// </remarks>
    private Node Descend(Solid solid)
    {
        Matrix4x4 saved = _ancestorTransform;

        // Row-vector convention: a point in the child's space is transformed by the child
        // first, then by its ancestors, so the child multiplies on the left.
        _ancestorTransform = solid.Transform.Matrix * _ancestorTransform;

        Node node = solid.Accept(this);

        _ancestorTransform = saved;
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
        AppendEdges(prism.Points, prism.ContourSizes, prism.Smooth);
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
            points: prism.Points,
            contours: prism.ContourSizes);
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
        AppendEdges(lathe.Points, lathe.ContourSizes, lathe.Smooth);

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
            paramB: lathe.Points.Count,
            spans: lathe.Points.Count,
            points: lathe.Points,
            contours: lathe.ContourSizes);
    }

    /// <summary>
    /// A general quadratic surface: three texels of coefficients, and no canonical form at all,
    /// because the coefficients <b>are</b> the shape.
    /// </summary>
    /// <remarks>
    /// The box is <see cref="Aabb.Unbounded"/>, which is what <c>plane</c> passes and for the
    /// same reason: a hyperboloid genuinely runs to infinity, and a box that is too small
    /// removes geometry from the image. Fitting a tight ellipsoid box in the cases where one
    /// exists is real work with several degenerate cases, and the language already carries the
    /// answer — <c>intersection { quadric { … } box { … } }</c> takes the box's bounds through
    /// <see cref="Aabb.Intersect"/>, which is exactly what POV-Ray's <c>bounded_by</c> is for.
    /// </remarks>
    public Node VisitQuadric(Quadric quadric)
    {
        _shapeOffset = _shapeData.Count / GpuLayout.ShapeStride;

        // (A, B, C, J), (D, E, F, 0), (G, H, I, 0). The constant rides in the first texel's
        // spare lane rather than taking a fourth.
        _shapeData.Add(quadric.Squared.X);
        _shapeData.Add(quadric.Squared.Y);
        _shapeData.Add(quadric.Squared.Z);
        _shapeData.Add(quadric.Constant);

        _shapeData.Add(quadric.Mixed.X);
        _shapeData.Add(quadric.Mixed.Y);
        _shapeData.Add(quadric.Mixed.Z);
        _shapeData.Add(0f);

        _shapeData.Add(quadric.Linear.X);
        _shapeData.Add(quadric.Linear.Y);
        _shapeData.Add(quadric.Linear.Z);
        _shapeData.Add(0f);

        return EmitLeaf(
            quadric,
            PrimitiveKind.Quadric,
            _ancestorTransform,
            Aabb.Unbounded,
            spans: 2,
            balls:
            [
                new Vector4(quadric.Squared, quadric.Constant),
                new Vector4(quadric.Mixed, 0f),
                new Vector4(quadric.Linear, 0f),
            ]);
    }

    /// <summary>A blob: a threshold texel followed by two texels per component.</summary>
    /// <remarks>
    /// Every component is written as a capsule, a spherical one with both ends at one point.
    /// The shading path then needs no discriminator: clamping the projection onto a segment of
    /// no length gives the segment's own point, so one closest-point expression covers both.
    /// </remarks>
    public Node VisitBlob(Blob blob)
    {
        _shapeOffset = _shapeData.Count / GpuLayout.ShapeStride;

        _shapeData.Add(blob.Threshold);
        _shapeData.Add(0f);
        _shapeData.Add(0f);
        _shapeData.Add(0f);

        // A component's field is exactly zero at its own radius and beyond, which is what makes
        // a blob local — and what makes the union of its components' capsules a true bound.
        Aabb bounds = Aabb.Empty;
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

            bounds = Aabb.Union(bounds, Aabb.AroundSphere(component.Base, component.Radius));
            bounds = Aabb.Union(bounds, Aabb.AroundSphere(component.Cap, component.Radius));
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
            caps: caps);
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

    /// <summary>
    /// A mesh: a header, a BVH over its triangles, the triangles, and the vertices they index.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The first leaf whose geometry does <b>not</b> also go into the generated source. Every
    /// other list-shaped primitive writes its points twice, once as a <c>const</c> array the span
    /// path reads and once into the shape buffer the normal path reads, and that is affordable
    /// because the lists are short. A bunny's is not: a hundred thousand <c>vec4</c> literals is
    /// a program no driver would take, and unrolling a loop over them is the failure iteration 14
    /// was built to avoid. So both paths read the buffer, and what the source carries is one
    /// offset.
    /// </para>
    /// <para>
    /// That offset is the whole of a mesh's identity in the emitted text, which is a problem
    /// where two roots are compared by what they emit: inside a probe every mesh sits at offset
    /// zero. The signature travels to <see cref="LeafEmitter"/> for that reason and is written
    /// into the body as a comment. See <c>MeshFile.Signature</c>.
    /// </para>
    /// <para>
    /// The span count is the mesh's declared <c>maxSpans</c> rather than a count of anything. It
    /// is the one bound here that is not exact, and documents/csg-raytracing.md says so in the
    /// same section where the tessellated curves it joins are already relaxed.
    /// </para>
    /// </remarks>
    public Node VisitMesh(Mesh mesh)
    {
        Aabb bounds = Aabb.Empty;

        foreach (Vector3 position in mesh.Positions)
        {
            bounds = Aabb.Union(bounds, new Aabb(position, position));
        }

        AppendMesh(mesh);

        // paramA is the shape offset for every list-shaped primitive, but only in the uploaded
        // record: the leaf body reads it from the plan, and a mesh is the one whose body needs
        // it, so it is passed rather than left to EmitLeaf to substitute.
        return EmitLeaf(
            mesh,
            PrimitiveKind.Mesh,
            _ancestorTransform,
            bounds,
            paramA: _shapeOffset,
            paramB: mesh.TriangleCount,
            spans: mesh.MaxSpans,
            signature: mesh.Signature);
    }

    /// <summary>
    /// A height field: the block, then a leaf whose body marches it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The bounding box is the whole of what makes this a solid, and it is why it is written here
    /// rather than derived in the shader. The footprint is fixed at <c>[-1, 1]</c> in x and z, the
    /// floor is the scene's <c>base</c>, and the lid is the tallest sample. Inside that box the
    /// solid is exactly <c>y ≤ H(x, z)</c>, so the four walls and the floor are the box's own
    /// faces and the march never has to find them: clipping the ray to the box already has.
    /// </para>
    /// <para>
    /// No canonical form and no matrix folding, for <see cref="VisitMesh"/>'s reason. A height
    /// field is not a unit anything either, and normalising the heights would put the grid's own
    /// extremes into the transform.
    /// </para>
    /// </remarks>
    public Node VisitHeightField(HeightField field)
    {
        AppendHeightField(field);

        Aabb bounds = new(
            new Vector3(-1f, field.Base, -1f),
            new Vector3(1f, Lid(field), 1f));

        // paramA is the shape offset in the uploaded record whatever is passed, but the body
        // reads it off the plan, so it is passed rather than substituted. Same as the mesh.
        return EmitLeaf(
            field,
            PrimitiveKind.HeightField,
            _ancestorTransform,
            bounds,
            paramA: _shapeOffset,
            paramB: field.Cells,
            spans: field.MaxSpans,
            signature: field.Signature);
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

            // Each operator bounds its result differently, and each is the tightest box that is
            // still a bound. Difference keeps the left operand's alone: removing material can
            // only shrink a solid.
            Aabb bounds = name switch
            {
                "Union" => Aabb.Union(accumulated.Bounds, right.Bounds),
                "Intersection" => Aabb.Intersect(accumulated.Bounds, right.Bounds),
                _ => accumulated.Bounds,
            };

            // A difference is written out as what it is — the intersection with a complement —
            // rather than as a third merge loop with its own way of being subtly wrong. The
            // complement is one span wider than what it inverts, and borrows a slot to hold it.
            Node inverted = default;

            if (name == "Difference")
            {
                inverted = Take(right.Spans + 1, right.Bounds);
                _body.Line($"{_spans.Complement(Ref(right), Ref(inverted))}();");
                _inlined += SpanLibrary.ComplementCost;
            }

            Node result = Take(spans, bounds);
            Node operand = name == "Difference" ? inverted : right;

            string call = name switch
            {
                "Union" => _spans.Union(Ref(accumulated), Ref(operand), Ref(result)),
                _ => _spans.Intersection(Ref(accumulated), Ref(operand), Ref(result)),
            };

            _inlined += name == "Union" ? SpanLibrary.UnionCost : SpanLibrary.IntersectionCost;

            _body.Line($"{call}();");
            _body.Line();

            if (name == "Difference")
            {
                Release(inverted);
            }

            Release(accumulated);
            Release(right);

            accumulated = result;
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
        IReadOnlyList<int>? contours = null,
        IReadOnlyList<Vector4>? balls = null,
        IReadOnlyList<Vector4>? caps = null,
        string signature = "")
    {
        if (!Matrix4x4.Invert(toWorld, out Matrix4x4 toLocal))
        {
            _failed = true;
            _diagnostics.Error(
                solid.Origin,
                $"'{solid.Kind.ToLowerInvariant()}' has a transform that cannot be inverted; "
                + "a zero scale collapses the solid to nothing");

            return new Node("s1_0", 1, Aabb.Empty);
        }

        int index = LeafCount;

        // The primitive record is unchanged and is still uploaded, for the shading path only:
        // a normal is recomputed once per hit, from whichever surface turned out to be visible.
        // Its two parameter slots mean what they always did — a ratio for the cone and the
        // torus, an offset and a count into the shape buffer for the four defined by a list and
        // for the quadric's coefficients — because the shading code that reads them is
        // unchanged. The generated span code reads neither: its taper, its minor radius, its
        // threshold and its coefficients are all literals.
        // A mesh and a height field are the exceptions to the last sentence: their span code reads
        // the buffer too, since a hundred thousand triangles and a million samples cannot be
        // literals. They still take the offset from the same slot as everything else here.
        bool usesShapeBuffer = kind is PrimitiveKind.Prism or PrimitiveKind.Lathe
            or PrimitiveKind.Blob or PrimitiveKind.SphereSweep or PrimitiveKind.Quadric
            or PrimitiveKind.Mesh or PrimitiveKind.HeightField;

        if (_leafOrdinal == 0)
        {
            _firstLeaf = toWorld;
        }

        int slot = _leafSlots[_leafOrdinal++];
        _leafShapes.Add(_emitted.Count);

        _primitives.Add((float)kind);
        _primitives.Add(_slotIndices is null ? slot : _slotIndices[slot]);
        _primitives.Add(usesShapeBuffer ? _shapeOffset : paramA);
        _primitives.Add(paramB);
        AppendRows(_primitives, toLocal);

        Node result = Take(spans, canonicalBounds.Transformed(toWorld));

        _inlined += _leafEmitter.Write(_leaves, new LeafPlan(
            index,
            kind,
            spans,
            toLocal,
            paramA,
            points ?? [],
            contours ?? [],
            balls ?? [],
            caps ?? [],
            $"{solid.Kind.ToLowerInvariant()} — leaf {index}",
            signature),
            Ref(result));

        _body.Line($"leaf{index}(ro, rd);");
        _body.Line();

        return result;
    }

    /// <summary>
    /// Writes closed contours as a header, a range per contour and one texel per edge, and
    /// remembers where the header started.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Still uploaded even though the span path now carries the same points as a <c>const</c>
    /// array: the shading path walks the contour to build a normal, and that is one evaluation
    /// per hit rather than one per ray per edge.
    /// </para>
    /// <para>
    /// The header is what the smooth flag used to ride in the sign of the segment count for.
    /// That trick was the only slot left when the primitive record had two, and it could carry
    /// one bit; the contour ranges are several numbers, so they need somewhere real to live, and
    /// once there is a header the flag belongs in it. Two texels for the usual single-contour
    /// prism, in a buffer only the shading path reads.
    /// </para>
    /// </remarks>
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
            // Relative to the first edge, not to the header, so the shader adds one base and
            // not two.
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

    /// <summary>
    /// Writes one mesh's block into the shape buffer, or points at the copy already there.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every offset in the header is <b>relative to the header itself</b>, which is what lets two
    /// leaves share one block: the reader adds its own base and needs to know nothing about where
    /// in the buffer it landed.
    /// </para>
    /// <code>
    /// [0]  (triangles, nodes, flags, 0)          flags bit 0: vertex normals follow
    /// [1]  (nodeAt, triangleAt, vertexAt, normalAt)
    ///      nodes:     two texels each, (min, escape) and (max, triangle)
    ///      triangles: one texel each, (i0, i1, i2, 0), in the tree's order
    ///      vertices:  one texel each, (x, y, z, 0)
    ///      normals:   one texel per vertex, only when the flag says so
    /// </code>
    /// <para>
    /// Indexed rather than three vertices written out per triangle. The bunny is 70 thousand
    /// triangles over 35 thousand vertices, so this is half the memory and, more to the point, a
    /// cache hit on the second triangle to reach a vertex. A float carries an integer index
    /// exactly to 2^24, which is an order of magnitude past
    /// <see cref="GpuLayout.MaxMeshTriangles"/>.
    /// </para>
    /// <para>
    /// The node texels are laid out exactly as <see cref="PackInstances"/> lays out the scene's
    /// own tree, which is why <see cref="GpuLayout.NodeStride"/> is not restated here. What
    /// differs is only what the second texel's last lane names.
    /// </para>
    /// </remarks>
    private void AppendMesh(Mesh mesh)
    {
        if (_tables.BlockOffsets.TryGetValue(mesh.Signature, out int existing))
        {
            _shapeOffset = existing;
            return;
        }

        var boxes = new List<Aabb>(mesh.TriangleCount);

        for (int t = 0; t + 2 < mesh.Indices.Count; t += 3)
        {
            Vector3 a = mesh.Positions[mesh.Indices[t]];
            Vector3 b = mesh.Positions[mesh.Indices[t + 1]];
            Vector3 c = mesh.Positions[mesh.Indices[t + 2]];

            boxes.Add(new Aabb(
                Vector3.Min(a, Vector3.Min(b, c)),
                Vector3.Max(a, Vector3.Max(b, c))));
        }

        Bvh.Result bvh = Bvh.Build(boxes);
        bool smooth = mesh.Normals is not null;

        _shapeOffset = _shapeData.Count / GpuLayout.ShapeStride;
        _tables.BlockOffsets[mesh.Signature] = _shapeOffset;

        int nodeAt = 2;
        int triangleAt = nodeAt + (bvh.Nodes.Count * 2);
        int vertexAt = triangleAt + mesh.TriangleCount;
        int normalAt = vertexAt + mesh.Positions.Count;

        _shapeData.Add(mesh.TriangleCount);
        _shapeData.Add(bvh.Nodes.Count);
        _shapeData.Add(smooth ? 1f : 0f);
        _shapeData.Add(0f);

        _shapeData.Add(nodeAt);
        _shapeData.Add(triangleAt);
        _shapeData.Add(vertexAt);
        _shapeData.Add(smooth ? normalAt : 0f);

        foreach (Bvh.Node node in bvh.Nodes)
        {
            _shapeData.Add(node.Bounds.Min.X);
            _shapeData.Add(node.Bounds.Min.Y);
            _shapeData.Add(node.Bounds.Min.Z);
            _shapeData.Add(node.Escape);

            _shapeData.Add(node.Bounds.Max.X);
            _shapeData.Add(node.Bounds.Max.Y);
            _shapeData.Add(node.Bounds.Max.Z);

            // A leaf's slot in the tree's order, not the triangle's place in the file: the
            // triangles are written below in that same order, so this indexes them directly.
            _shapeData.Add(node.Item);
        }

        foreach (int triangle in bvh.Order)
        {
            _shapeData.Add(mesh.Indices[triangle * 3]);
            _shapeData.Add(mesh.Indices[(triangle * 3) + 1]);
            _shapeData.Add(mesh.Indices[(triangle * 3) + 2]);
            _shapeData.Add(0f);
        }

        foreach (Vector3 position in mesh.Positions)
        {
            _shapeData.Add(position.X);
            _shapeData.Add(position.Y);
            _shapeData.Add(position.Z);
            _shapeData.Add(0f);
        }

        if (mesh.Normals is null)
        {
            return;
        }

        foreach (Vector3 normal in mesh.Normals)
        {
            _shapeData.Add(normal.X);
            _shapeData.Add(normal.Y);
            _shapeData.Add(normal.Z);
            _shapeData.Add(0f);
        }
    }

    /// <summary>
    /// One height field's block, written once however many placements share it.
    /// </summary>
    /// <remarks>
    /// <code>
    /// [0]  (cells, flags, maxSteps, 0)          flags bit 0: shade with interpolated normals
    /// [1]  (heightAt, 0, base, high)
    ///      heights: (cells + 1)² samples, FOUR to a texel, row major with z outermost,
    ///               the tail padded with the last sample
    /// </code>
    /// <para>
    /// Four samples to a texel rather than one. A height is a single number, so a texel apiece
    /// would spend three lanes on nothing: at the cap that is 16.8 MB against 4.2. The shader
    /// picks the lane with a chain of <c>?:</c> and never with a variable index, for the reason
    /// written on <c>meshPermute</c>, and it does it inside a hand-written helper so the emitted
    /// body never pays for it.
    /// </para>
    /// <para>
    /// The tail is padded with the last sample rather than with zero. Nothing reads past the end,
    /// so the value is arbitrary, and repeating the last one keeps a dump of the buffer readable
    /// instead of ending in a cliff that looks like a bug.
    /// </para>
    /// <para>
    /// <c>maxSteps</c> is the march's trip bound and it is packed rather than computed in the
    /// shader. Either would work, because a value derived from a fetched one is still a runtime
    /// bound and iteration 15 counts such a loop at a constant; packing it says what it is.
    /// </para>
    /// <para>
    /// There is deliberately <b>no acceleration structure</b>. A mesh needs a tree because its
    /// triangles are in no order; a grid is its own index, and the march visits exactly the cells
    /// the ray crosses. What that costs is the grazing ray, which walks cells it is far above;
    /// what it saves is the second tree, the second packing and the second set of tie-breaks. See
    /// documents/height-fields.md.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Where the box that clips the march is closed off above, which is <b>strictly</b> above
    /// every sample.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The lid is not a surface of the solid: the solid is everything from <c>base</c> up to the
    /// terrain, and the terrain is what a ray hits. So raising the lid changes no geometry at all.
    /// What it decides is a question the march asks once per ray, and cannot afford to get wrong:
    /// <i>is the ray already inside where it enters the box.</i>
    /// </para>
    /// <para>
    /// At the true maximum the surface <b>touches</b> the lid, and a ray entering there is exactly
    /// on the boundary. Whether it counts as inside then turns on the last bit of
    /// <c>origin + t * direction</c>, and getting it wrong opens a span at the lid instead of at
    /// the terrain. The symptom is a flat grey patch spreading from the tallest point over
    /// however much of the surface comes near it, which for a rounded summit is a wide band and
    /// not a pixel. Lifting the lid by a ten-thousandth of the solid's own height removes the tie
    /// rather than trying to resolve it.
    /// </para>
    /// <para>
    /// Relative to the height rather than absolute, because a terrain may be a millimetre or a
    /// kilometre tall before its transform, and floored at one unit so that a flat field, whose
    /// height is zero, still gets a lid it can be told apart from.
    /// </para>
    /// </remarks>
    private static float Lid(HeightField field) =>
        field.High + (MathF.Max(field.High - field.Base, 1f) * 1e-4f);

    private void AppendHeightField(HeightField field)
    {
        if (_tables.BlockOffsets.TryGetValue(field.Signature, out int existing))
        {
            _shapeOffset = existing;
            return;
        }

        _shapeOffset = _shapeData.Count / GpuLayout.ShapeStride;
        _tables.BlockOffsets[field.Signature] = _shapeOffset;

        const int heightAt = 2;

        _shapeData.Add(field.Cells);
        _shapeData.Add(field.Smooth ? 1f : 0f);

        // A ray crosses at most one boundary per axis per cell, so a walk of a grid this wide
        // takes at most twice its width in steps, plus the one that leaves it.
        _shapeData.Add((2 * field.Cells) + 2);
        _shapeData.Add(0f);

        _shapeData.Add(heightAt);
        _shapeData.Add(0f);
        _shapeData.Add(field.Base);
        _shapeData.Add(Lid(field));

        IReadOnlyList<float> heights = field.Heights;

        for (int i = 0; i < heights.Count; i += 4)
        {
            _shapeData.Add(heights[i]);
            _shapeData.Add(heights[Math.Min(i + 1, heights.Count - 1)]);
            _shapeData.Add(heights[Math.Min(i + 2, heights.Count - 1)]);
            _shapeData.Add(heights[Math.Min(i + 3, heights.Count - 1)]);
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

    private static void AppendRows(List<float> target, Matrix4x4 m)
    {
        target.Add(m.M11); target.Add(m.M12); target.Add(m.M13); target.Add(m.M14);
        target.Add(m.M21); target.Add(m.M22); target.Add(m.M23); target.Add(m.M24);
        target.Add(m.M31); target.Add(m.M32); target.Add(m.M33); target.Add(m.M34);
        target.Add(m.M41); target.Add(m.M42); target.Add(m.M43); target.Add(m.M44);
    }

    /// <summary>
    /// Appends one appearance's materials as a contiguous run and returns where it starts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Materials used to be interned one at a time, because a leaf named one by absolute index
    /// and nothing cared where it sat. A shared shape's leaf names a <i>slot</i> instead and the
    /// instance names the base, so <c>base + slot</c> has to land on the right entry, which
    /// makes the run, not the material, the thing that has to be contiguous and therefore the
    /// thing that is deduplicated.
    /// </para>
    /// <para>
    /// Two appearances wearing the same materials in the same order share a run, so sixteen ivory
    /// pawns cost one. Two runs that merely overlap do not, and the few duplicated entries that
    /// costs are sixteen floats each against a table nothing iterates.
    /// </para>
    /// </remarks>
    private int InternMaterials(IReadOnlyList<Material> run)
    {
        string key = string.Join(',', run.Select(IdOf));

        if (_materialRuns.TryGetValue(key, out int existing))
        {
            return existing;
        }

        int index = _materials.Count / GpuLayout.MaterialStride;
        _materialRuns[key] = index;

        foreach (Material material in run)
        {
            AppendMaterial(material);
        }

        return index;
    }

    /// <summary>Where each slot of a folded appearance lands in the material table.</summary>
    /// <remarks>
    /// The check is the seam between two things worked out separately:
    /// <see cref="ShapeCanonicalizer"/> decides the slot pattern, and the signature is what
    /// promises every appearance of a shape shares it, and a mismatch here would be read off the
    /// end of a run as whatever material happened to follow it. That is a wrong colour with
    /// nothing to point at, so it is worth the four lines that make it an exception instead.
    /// </remarks>
    private int[] AbsoluteSlots(IReadOnlyList<Material> run, IReadOnlyList<int> leafSlots)
    {
        int needed = leafSlots.Count == 0 ? 0 : leafSlots.Max() + 1;

        if (needed > run.Count)
        {
            throw new InvalidOperationException(
                $"a shape whose leaves name {needed} material slots was matched with an appearance "
                + $"wearing {run.Count}; two shapes were treated as one that are not.");
        }

        int start = InternMaterials(run);
        return [.. Enumerable.Range(start, run.Count)];
    }

    /// <summary>A stable number per distinct material, for naming a run rather than finding one.</summary>
    private int IdOf(Material material)
    {
        if (_materialIndices.TryGetValue(material, out int existing))
        {
            return existing;
        }

        int id = _materialIndices.Count;
        _materialIndices[material] = id;
        return id;
    }

    private void AppendMaterial(Material material)
    {
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
    }
}
