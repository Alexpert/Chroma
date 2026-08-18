# Meshes

How a triangle mesh becomes a solid: what the decoders read, what the topology pass refuses, and
what the emitter uploads. The tracing itself is in
[csg-raytracing.md](csg-raytracing.md#mesh--parity-in-three-dimensions), because it is one more
entry in the list of primitives and belongs beside the others.

Built in iteration 24. The user-facing half is in
[scene-primitives.md](scene-primitives.md#mesh) and [manual.md](manual.md#a-shape-from-a-file).

## Why a decoder is the small half

The obvious summary of this iteration is "read OBJ and STL". Both are afternoon formats: OBJ is
lines of numbers, STL is a fixed-size record repeated. Neither is where the work went.

The work went into one sentence from iteration 6, which refused POV-Ray's `open` cones and prisms
and has been true of every shape since: **a CSG operand needs a well-defined inside.** Every other
primitive here satisfies it by construction, because it is described by an equation or by a
contour that closes on its own. A mesh is described by a file, and a file can say anything.

So a mesh is the first primitive that can be refused for what it *contains* rather than for what
one of its fields says, and the refusal has to be specific enough to act on. That is
`Chroma.Core/Assets/MeshTopology.cs`.

## The three failures are one table

Take every triangle's three edges as **directed** pairs, in winding order. On a closed, manifold,
consistently oriented surface each directed edge appears exactly once and its reverse exactly
once, because the two triangles sharing an edge traverse it in opposite directions.

Every way of failing is a reading of that one table:

| Reading | Meaning | Repairable |
| --- | --- | --- |
| `count(a→b) == 1`, `count(b→a) == 0` | a hole: this edge has one triangle on it | yes |
| `count(a→b) > 1` | two neighbours wind the same way: they disagree about which side is out | no |
| `count(a→b) + count(b→a) > 2` | three or more triangles on one edge: not manifold | no |

One dictionary, one pass, and the diagnostic can name a count and a position because it has both.

**Only the hole is repaired, and only when asked.** `close: true` fills each one with a fan of
triangles round its own centre. The other two are refused whatever `close` says. That is not
caution for its own sake: filling a hole in a mesh whose triangles disagree about which side is
out produces a solid with a definite inside that is definitely wrong, and a wrong picture with no
diagnostic is the failure mode this project spends the most effort avoiding.

### Finding the holes without walking them

The boundary edges are grouped by shared vertices rather than chained into ordered loops. The
grouping is all a fan needs, and a walk has to decide what to do at a vertex where two boundaries
touch, where the grouping simply puts them in the same hole.

A boundary edge `a → b` is missing its reverse, so the triangle that repairs it is
`(centre, b, a)`: that supplies `b → a`, and the two spokes `centre → b` and `a → centre` cancel
against the neighbouring triangles' own spokes all the way round. One new vertex per hole.

**The fan is not a proof**, and the code does not treat it as one. A boundary that pinches through
a single vertex chains into a figure of eight, and one fan over it repeats a directed edge. So the
whole table is rebuilt and read again after capping, and a mesh that is still not closed is
refused with the same diagnostic prefixed by `'close' could not close this mesh`.

## Welding, and why STL forces it

STL has no vertices, only corners: every facet writes its three points out in full, so a cube
arrives as thirty-six positions sharing nothing. Every edge of every STL is therefore a boundary
edge until the duplicates are merged, and without welding the format would be unusable.

Welding is done for every format, by **exact value**. A tolerance would need a scale, and the only
scale available is the model's own bounding box, which would make welding depend on how far from
the origin the file happened to be written. Every writer that emits an STL from a mesh emits the
same bits for the same shared vertex, and the two models in `scenes/assets/` both weld perfectly:
the bunny's 337,206 corners become 56,203 vertices, and `V - E + F = 2` on the result.

Degenerate triangles are dropped in the same pass. Two corners in the same place leave a triangle
with no area whose two remaining edges are each other's reverse, which would be counted as surface
here.

## What the file brings that no other shape has

Vertex normals. `smooth: true` interpolates them across each triangle, which is iteration 7's
lesson repeated: the tessellation is in the shading before it is in the silhouette, and a faceted
mesh reads as a coarse mesh when it is a missing interpolation. The bunny at 112,402 triangles is
the clearest case, because its triangles are smaller than a pixel and the faceted version reads as
noise rather than as facets.

**Where the normals come from is not the obvious answer.** OBJ indexes normals separately from
positions, so one position can be quoted with several, and the natural reading — one vertex per
distinct `v/vn` pair — tears the mesh apart topologically. Two triangles across a hard edge stop
sharing their vertices, every such edge counts as a boundary, and a perfectly closed model is
refused. So the topology is the **positions**, and a position's normal is the average of every
normal quoted for it. Where a file gives one normal per position, which is what a smoothed export
does, that reproduces the file exactly.

Where the file gives none, they are derived: the area-weighted average of the incident faces,
which falls out of not normalising the cross product. That weighting is the one worth having,
because a fine triangle and a coarse one meeting at a vertex describe the same surface.

**UV coordinates are read past and discarded.** Nothing consumes them until textures land, and two
floats per vertex uploaded for no reader is bandwidth spent on nothing. `ObjReader` already parses
the `vt` line, so the place they would enter is marked.

## The second tree

`Compilation/Bvh.cs` was `InstanceBvh` until this iteration. It was always a hierarchy over a list
of boxes that knew nothing else about them, so a tree over triangles is the same call with
triangle boxes, and the rename says so. Binned SAH, one item per leaf, escape indices, and the
same two texels on the GPU, so `GpuLayout.NodeStride` serves both.

The one property worth restating is **escape indices, not a stack**. Nodes are laid out
depth-first, so descending is `++node` and skipping a subtree is a jump to an index the node
carries. A traversal stack declared in a leaf body would be storage allocated at every inlined call
site of that leaf, which is the `error C5041` recorded in [gpu-backends.md](gpu-backends.md).

## The block in the shape buffer

Written by `GeometryEmitter.AppendMesh`. Every offset is relative to the header, which is what lets
two placements share one copy.

```
[0]  (triangles, nodes, flags, 0)          flags bit 0: vertex normals follow
[1]  (nodeAt, triangleAt, vertexAt, normalAt)
     nodes:     two texels each, (min, escape) and (max, triangle)
     triangles: one texel each, (i0, i1, i2, 0), in the tree's order
     vertices:  one texel each, (x, y, z, 0)
     normals:   one texel per vertex, only when the flag says so
```

Indexed rather than three vertices written out per triangle: the bunny is 112,402 triangles over
56,203 vertices, so this is half the memory and, more to the point, a cache hit on the second
triangle to reach a vertex. A float carries an integer index exactly to 2²⁴, an order of magnitude
past `GpuLayout.MaxMeshTriangles`.

**Blocks are interned by content**, in `SceneTables.MeshOffsets`, keyed on the signature below.
It is the only table entry interned that way, and the reason is size: a contour is a handful of
texels and a bunny is 674,000, so writing one per placement is the difference between a scene that
fits on the card and one that does not. `scenes/meshes.chroma` holds two bunnies and uploads one.

## The signature, and a collision that would have been silent

This is the part of the iteration that was not visible from the entry, and it is worth writing
down because nothing about it is obvious from the code that needed fixing.

Two roots are decided to be **one shape** by emitting each into a throwaway `GeometryEmitter` and
comparing the GLSL text; `LeafEmitter.KeyOf` deduplicates shared bodies the same way. That works
because every other primitive's geometry ends up in the source it emits, so the comparison cannot
be wrong: what a shape *is* is defined as what it emits.

A mesh breaks the assumption. Its geometry is in a buffer, and its body carries one literal offset
into that buffer. Inside a probe every buffer starts empty, so **every mesh sits at offset zero**
and any two of them emit identical text. A teapot and a bunny in one scene would have compared
equal, and the second would have been drawn as the first, with nothing to say so.

The fix is a content hash — `MeshFile.Signature`, sixteen hex characters over the positions, the
indices and the smooth flag — carried on `LeafPlan`, appended to `KeyOf`, and written into the
emitted body as a comment:

```glsl
// mesh 6f3a1c9e0b47d2a5
```

A comment costs nothing: `GlslWriter` does not count a line starting with `//`, so the cost model
is untouched, and both comparisons become correct again. `MeshTests.Tells_two_different_meshes_apart`
is the regression test, and it fails loudly without the signature.

Over the geometry rather than over the file's bytes, because the file is not what is uploaded: the
same model welded, capped and smoothed differently is a different mesh, and two files that decode
to the same triangles are the same one.

## What it costs

Measured on `scenes/meshes.chroma`, an RTX 4070 SUPER, two teapots and two bunnies:

| | |
| --- | --- |
| Triangles | 6,480 × 2 and 112,402 × 2 |
| Estimated cost | 343 statements, 1% of the instruction budget |
| Widest root | 5 spans |

**A mesh costs the program almost nothing and the card a great deal**, which is the reverse of
every other primitive here. The traversal loop takes its bound from the shape buffer rather than
from a literal, so the driver compiles one tree step instead of one per node, and iteration 15
counts a loop bounded by a runtime value at a constant. `MeshTests.Costs_the_same_however_many_triangles_it_holds`
asserts it directly: two meshes differing fourfold in size report the same `ShapeReport.Cost`.

What grows is memory. The bunny's block is about 674,000 texels, which is 10.8 MB on the card, and
`GpuLayout.MaxMeshTriangles` at two million is a limit on that rather than on anything the driver
counts.

## What was left out

**glTF and PLY.** Both are better long-term answers than STL, and both are binary formats with real
structure rather than an afternoon's parsing. OBJ and STL cover what this iteration needed to
deliver and what most models are published as.

**A distance field.** `--sdf` refuses a mesh. The exact distance to a triangle mesh is a second walk
of the same tree with its own nearest-point test, plus a sign that is either a winding number or a
ray cast — and a ray cast is the span backend. That backend exists to answer a question about
iteration 0's choice rather than to render production images, and the primitives that already have
both answers are what answers it. See [raymarching.md](raymarching.md).

**An exact span bound.** `maxSpans` is declared by the scene rather than derived, and it is the one
bound in this renderer that is not a proof. A mesh's true worst case is one span per two triangles.
See the note in [csg-raytracing.md](csg-raytracing.md#mesh--parity-in-three-dimensions).
