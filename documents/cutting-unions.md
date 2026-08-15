# Cutting inside a top-level `union`

How the last scene the renderer could not draw came to draw, what the cut costs, and where it
declines to make one.

## The scene this exists for

`scenes/cube.chroma` is twenty-one lines of recursion. `cube(3)` returns a `union` of twenty
`object`s, each holding a `cube(2)`, each holding a `cube(1)`, each holding twenty boxes: a
Menger-like sponge of eight thousand solids, written by a function calling itself three times.

It was refused for four iterations, and each mechanism that moved the ceiling moved past it rather
than through it.

- **Sharing** (`ShapeCanonicalizer`) asks whether two *roots* are the same shape standing somewhere
  else. `cube.chroma` has one root. There is nothing to compare it to.
- **Chunking** (`SceneChunker`) cuts between whole shapes. `cube.chroma` is one shape. There is
  nothing to cut between.
- The **budget** (`ShapeCost`) could tell you in advance that the scene was hopeless, and did:
  1360% of what a program may weigh. Knowing is not the same as helping.

So the scene compiled to 157,628 lines of GLSL, the driver spent between 134 and 149 seconds on it,
and the answer was `fatal error C9999: *** exception during compilation ***`. Because a driver
caches what it compiled and never what it refused, it paid that on every run, forever.

## The observation

**A scene's roots are already an implicit union, and they are already resolved separately.**

`Scene.Roots` is a list, and `documents/csg-raytracing.md` records the choice that follows from it:
roots are unioned, but each is resolved into its own span list rather than merged into one.
`GeometryEmitter.EmitShape` gives every root its own function, its own bounding-box test and its own
list; `traceScene` folds each into the running nearest hit and moves on. A scene of nine separate
spheres therefore costs a one-span list nine times, where a `union` of nine spheres costs a
nine-span list once.

Which means cutting inside a top-level `union` needs no new mechanism at all. Rewriting

```
union { a b c }
```

as three roots says exactly the thing the renderer is already built to say. That is the whole of
`RootSplitter`.

## What the cut is actually for

The interesting part is what it is *not* for. The obvious reading of "cut inside a union" is that a
scene too big for one program gets spread over several, which is what chunking does one level up.
That is not what happens here, and it is not what makes `cube.chroma` renderable.

The pieces of a cut union go back to `ShapeCanonicalizer`, which then sees what it could never see
before: the twenty sub-cubes of `cube(3)` are **the same shape standing in twenty places**. Cutting
does not divide the scene. It exposes the repetition that was inside one shape all along, and hands
it to instancing, which collapses it.

## Round by round

Every number here is measured on this machine, an RTX 4070 SUPER, by compiling `cube.chroma` and by
compiling `cube(1)` and `cube(2)` as scenes of their own.

| round | roots | shape | placements | estimate | share of budget | widest root | generated lines |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 0, as written | 1 | 8,000 leaves | 1 | 272,005 | **1360%** | 8,000 spans | 157,628 |
| 1 | 20 | 400 leaves | 20, shared | 13,605 | 68% | 400 spans | about 11,000 |
| 2, final | 400 | 20 leaves | 400, shared | **685** | **3%** | **20 spans** | **1,626** |

The scene the driver refused after 140 seconds now compiles in about one second and renders at
**110.9 samples/s at 1280x720**. Nothing in the shader changed; nothing about how a box is
intersected changed. It was rewritten from one shape of eight thousand leaves into four hundred
appearances of a shape of twenty, and that is the entire difference.

### Why round 1 is not the end of it

Round 1 already fits: 68% of the instruction budget, comfortably compilable. Stopping there would
have been a mistake, and it is worth being explicit about why, because the estimate does not see it.

A span list is **state a thread carries**, not code the driver compiles. A 400-span list is roughly
six kilobytes of it per thread before anything else in the path tracer is counted, and the widest
root anywhere else in the repository is `sweeps` at 24, already the heaviest register load in the
set. A scene can sit well inside the instruction budget and still be hopeless for a reason the
instruction budget does not measure.

So the loop has two stopping conditions, and `ShapeCost.MaxSpans` is the second. It is set to 32
because that clears every scene in the repository with room to spare, and it is a **target for
cutting rather than a limit a scene can fail**: a forty-segment `lathe` is one leaf, has no seam to
cut on, and compiles exactly as it always did.

## The rule

`RootSplitter.Cut` runs before `SceneChunker`, and cuts a shape when it

1. **cannot fit a program on its own** (`Weight > budget`), or
2. **declares a span list wider than `ShapeCost.MaxSpans`**,

and only ever for a scene that does not fit at all. A scene inside the budget returns from the first
line untouched, by the same shortcut and for the same reason `SceneChunker.Split` takes one.

Note what condition 1 is *not*. A scene merely over budget **in aggregate** is not cut. That is what
chunking is for, and the two costs are not the same: a chunk costs a second pass of the path tracer,
a cut costs the coalescing between two operands. `palisade.chroma` is two hundred hexagonal posts
of which not one is near the budget, and it goes through this untouched and is chunked exactly as it
was.

Each round strictly reduces the depth of the deepest cuttable union, so the loop terminates; a round
that cuts nothing returns immediately.

## What a cut costs

**Coalescing.** Two operands of one `union` that overlap merge into a single interval, and the faces
buried inside the merged region stop existing. As separate roots they do not merge, and each is
resolved on its own.

This is not a new limitation. It is the one `documents/csg-raytracing.md` already records for roots
that were written separately, and that `documents/implementation.md` lists twice under "wrap them in
an explicit `union`". What is new is that a `union` written by hand can now become separate roots
without the author asking, so the cut has to be careful where the difference is visible.

- For an **opaque** pair it is invisible from outside: the nearer entry is the nearer entry whether
  the intervals were merged or not. The case it does get wrong is a ray that *starts inside both at
  once*, which then leaves at a surface interior to the union. That is accepted knowingly.
- For a **transmissive** pair it is a lens-shaped seam where the two solids cross, which is a
  picture changing rather than a bound loosening. `scenes/glass.chroma` exists in part to show that
  two overlapping glass spheres under a `union` have no such seam.

So `Cuttable` is a test on the operands and not a blanket rule:

> A `union` is cut unless two of its operands **can be transmissive** and their **bounds overlap**.

Both halves matter. Refusing to cut any union holding glass would leave two panes at opposite ends
of a room uncuttable for a merge that can never happen; refusing only on overlap would silently
change what glass looks like. Bounds are computed at all only when two operands could be
transmissive, so a scene of plain solids pays one walk over the materials and no probes.
"Transmissive" counts `scattering` as well as `transmission`: a participating medium is entered and
left, so where its boundary sits is as visible as a glass one's.

When a cut is declined, the shape stays whole and the driver may well refuse it. That is the right
way round. A refusal is a message; a wrong picture is not.

## Where it sits

```
Scene.Roots
   |
   v
RootSplitter.Cut ......... cut a shape no program can hold into its union's operands
   |                       (a scene that fits passes straight through)
   v
ShapeCanonicalizer ....... which roots are the same shape standing somewhere else
   |
   v
ShapePartition.Choose .... which shapes are reached through the instance buffer
   |
   v
SceneChunker.Split ....... how many programs it takes
   |
   v
GeometryEmitter .......... the GLSL
```

`RootSplitter.Cut` owns the first three of those steps, because deciding whether to cut requires
knowing what a shape costs, which requires partitioning, which requires the roots. It returns the
finished `ShapePartition` rather than the roots so that the two cannot drift apart and so that a
large scene is not partitioned twice.

`Scene.Roots` is read and never written. The hierarchy dump shows the file as written, and a cut is
a decision about how to compile a scene rather than a change to it.

### Rebuilding a root

`ShapeCanonicalizer.Spine` returns the chain of single-operand nodes down the top of a root, ending
in the shape itself. `Peel` is the fold of that chain, and the cut is the rebuild of it: each operand
is wrapped back in a clone of the same chain, so the union's own `scale:` and its own material land
on every piece and are inherited exactly as they were. Nothing is composed and nothing is inverted.
A `Transform` is immutable and passed by reference, so there is no arithmetic here that could round.

Every wrapper is rebuilt as a `union` of one whatever it was, which loses nothing: an `intersection`
or a `difference` of a single operand is that operand, and `Peel` walks them all off again before
anything is emitted.

One side effect is better diagnostics. What a report points at afterwards is whatever the operand
peels down to, carrying its own origin and its own generating loop, so a refusal names the box and
the `for` that made it rather than the `union` at the top of the file.

## Saying that it happened

The console line reports the cut, because it is the one decision in the compiler that can change
what a picture looks like:

```
cube.chroma: 20 primitives, 1 shapes, 400 instances, 1 materials, 1 lights, 1626 generated lines,
widest root 20 spans, cut into 400 roots from 1, estimated 685 statements (3% of the instruction
budget); lean shader
```

## What did not change

Every scene in the repository is compiled to the same bytes it was before, which is a property of
the code rather than a measurement: a scene inside the budget never reaches the cut. `cornell`,
`chess-full`, `glass` and `palisade` were rendered at a fixed sample count before and after and
compared byte for byte. The `--sdf` backend does not partition at all and is untouched.

## Open questions

- **The cut is not spatial.** Four hundred pieces are packed into a BVH by
  `InstanceBvh`, which is spatial, so this matters less than it might. But which operands become
  which roots is decided by nothing at all, and a `union` of two distant halves is cut the same way
  as a `union` of two interleaved ones.
- **Nothing unshares afterwards.** `cube.chroma` at round 2 is four hundred placements of a
  twenty-leaf shape. Round 3 would be eight thousand placements of a one-leaf shape, a program of
  about thirty-four statements and a one-span list, and a much deeper tree to walk. Nobody has
  measured which is faster; the width rule stops as soon as it is satisfied.
- **`MaxSpans` is one number chosen to clear the repository.** It has not been swept, and the
  quantity it stands in for, how much state a thread may carry before occupancy collapses, is a
  property of the GPU rather than of the scene.
- **The opaque overlap case is still wrong from inside.** A ray starting inside two overlapping
  opaque operands of a cut union leaves at an interior surface. It was already wrong for roots
  written separately, and no scene in the repository renders from inside one.
- **A shape with no seam still has no answer.** An `intersection` of eight hundred spheres, or a
  `lathe` of two thousand segments, is one shape that cannot be cut, cannot be shared and cannot be
  chunked. It is refused, with a diagnostic naming it. That is the whole of what is left of the
  ceiling.
