# Instancing: recovering shape identity

> **Status: done and measured.** `scenes/chess-full.chroma` compiles and renders. What the driver
> counts is one body per **distinct** shape rather than one per root, so a scene is bounded by how
> much *different* geometry it holds. Repeating a piece is free, and the language did not change.

## What this changes, in one sentence

A scene stops being **one emitted body per root** and becomes **one body per distinct shape, plus
a buffer of placements and a tree over them**, with one hard boundary: a shape that stands in only
a few places is still emitted where it stands, exactly as before, because instancing is not free.

## Why

[gpu-backends.md](gpu-backends.md) recorded the ceiling per-scene code generation introduced: the
driver refuses a program past roughly 65,000 assembly instructions, counted *after* it has inlined
every call and unrolled every constant-bound loop. `chess-full.chroma` was kept in the repository
because it did not compile.

The cause was structural and visible in one function. `GeometryEmitter.WriteTraceScene` emitted one
guarded block per root, each with its shape folded in and its world matrix as a `const mat4`.
**Program size was O(roots).** A chess set writes the same pawn sixteen times and paid for sixteen
pawns.

Everything else had been tried and measured: a newer OpenGL refuses at the same line, the compute
path one instruction later, and sharing leaf *bodies* between identical solids cuts 29% of the
source and almost none of the ceiling, because the inliner puts the copies back.

## The mechanism

Two properties of the driver, and neither can be defeated by inlining:

- **A loop whose bound is a uniform expands its body exactly once**, whatever the trip count.
- **A `switch` on a runtime value is a real branch**, not a copy per case.

So `traceScene` becomes a stackless BVH walk bounded on `uNodeCount`, with one `case` per distinct
shape:

```glsl
while (node < uNodeCount)                      // uniform bound: not unrolled
{
    vec4 lo = NODE(node * NODE_TEXELS);
    vec4 hi = NODE(node * NODE_TEXELS + 1);

    if (!boundHit(ro, rd, lo.xyz, hi.xyz, min(maxT, best.t))) { node = int(lo.w); continue; }

    int instance = int(hi.w);
    if (instance < 0) { ++node; continue; }    // interior: descend

    int slot = instance * INSTANCE_TEXELS;
    mat4 toShape = fetchInstanceMatrix(slot);
    vec3 ros = (toShape * vec4(ro, 1.0)).xyz;
    vec3 rds = (toShape * vec4(rd, 0.0)).xyz;

    switch (int(INSTANCE(slot).x))             // one case per DISTINCT shape
    {
        case 0: shape0(ros, rds); resolve_s3_0(best, instance); break;
        ...
    }
    ++node;
}
```

Three things keep this from being a return to the interpreter that iteration 12 removed:

- **The folded `const mat4` survives inside every shape.** Only the outer placement is a fetch. A
  lathe's outline, its taper and its thresholds are still literals.
- **The span pool absorbs the switch.** Every shape already writes into shared file-scope span
  globals, so N cases are not N live allocations. This is why the switch does not bring back the
  register pressure that killed the shared shader.
- **No dynamically indexed span stack.** The interpreter's `stack[sp]`, the single largest cost
  code generation removed, does not come back. Only the instance and node reads are indexed, and
  they are buffer fetches rather than local memory.

## Recovering shape identity

The language gives no help, deliberately: a `let` stores an unbound block and a `function` returns
a fresh tree at every call, so `pawn(0, 1)` and `pawn(1, 1)` share no object identity. Nothing in
the model says two roots are the same piece. Identity is therefore **recovered** by the compiler,
which is what keeps the change out of the language.

`ShapeCanonicalizer` defines it this way:

> Two roots are the same shape when they **emit the same GLSL**.

That is the definition and not an approximation of one, which is why there is no list in that file
of what to compare about a sphere. What a solid *is* is what it compiles to, so `GeometryEmitter`
is asked: each root is emitted into a throwaway emitter and the result is the key. There is no
second description of a solid that can drift out of step with the first.

What has to come off first is everything about *where* the root stands, and it arrives in two
forms that look nothing alike in the source and identical to the emitter:

1. **On the spine.** `object { pawn(f, rank) material: ivory }` binds to a union of one carrying
   the material, wrapping a lathe carrying the `translate:`. Walking down single-operand nodes
   takes both off.
2. **Inside the primitives.** `sphere { center: p }`, `box { min: … max: … }` and
   `cylinder { base: … cap: … }` put the position in the solid's own fields, and this is the
   commoner idiom by far: it is how every square of a chessboard and every cell of a lattice is
   written. Nothing is on the spine to remove, so the shape is emitted once to find where its
   first leaf lands, and re-emitted normalised against that.

Both steps are **exact**. The spine transform is removed rather than divided out, and the second
normalisation is a pure translation, which cancels against its own negation to the last bit where a
general matrix inverse would not. Nothing rounds and nothing needs a tolerance, so two appearances
either agree completely or are honestly different.

Materials are excluded and travel with the placement. A leaf records a *slot*; an appearance
supplies what fills it. Without this an ivory pawn and an obsidian one would be two shapes, which
is exactly the sharing the work exists to get. How many distinct materials the leaves share out
between them **is** part of the shape, and is in the key.

## The threshold, which was not in the plan

Sharing everything shareable made small scenes slower, and by enough to matter: `glass` lost 35%
and `cornell` 18%. A BVH walk is a loop of **dependent** memory reads, where a run of folded guards
is independent work the compiler can interleave and the branch predictor can get right.

So a scene shares nothing until it holds **32 repeated placements**, and the question is asked of
the scene rather than of each shape, because what decides it is the depth of one tree over every
placement. A driver refusal overrides the threshold and recompiles with everything shared, since a
program that will not compile has no speed to protect. See `ShapePartition.DefaultShareFrom` and
`Program.CompileTracer`.

This is why eleven of the fourteen scenes in `scenes/` render **bit-identically** to the build
before this work: they are under the threshold, so they emit byte-for-byte what they emitted
before.

## What it cost and what it bought

| Scene | Primitives | Shapes | Instances | Generated lines | Before |
| --- | ---: | ---: | ---: | ---: | --- |
| `chess-full` | 162 → **32** | **10** | 96 | 7,434 → **3,342** | refused |
| `chess-half` | 126 → **32** | **10** | 80 | 6,436 → **3,342** | compiled |
| `lattice` | 425 → **20** | **8** | 124 | 10,904 → **1,027** | compiled |

Speed, 256 samples at 1280x720 on an RTX 4070 SUPER:

| Scene | Before | After | | Instances |
| --- | ---: | ---: | ---: | ---: |
| chess | 191.1 | 1106.7 | **5.79x** | 68 |
| lattice | 98.5 | 338.1 | **3.43x** | 124 |
| chess-half | 4.5 | 13.7 | **3.04x** | 80 |
| cornell | 643.7 | 673.7 | 1.05x | 0 |
| glass | 519.1 | 521.0 | 1.00x | 0 |
| colonnade | 664.7 | 662.9 | 1.00x | 0 |
| sweeps | 153.1 | 152.3 | 0.99x | 0 |

**The gain is the tree, not the sharing.** `traceScene` used to test every root's box in source
order, so a lattice of 125 cells paid 125 box tests per ray per bounce and now pays about seven.
Instancing is what makes a tree possible, since placements have to be data before anything can
sort them.

The price, stated plainly: the world matrix becomes four fetches for repeated geometry, bounding
boxes become per-instance data, and a scene under the threshold gets none of either.

## One prediction that was wrong

[gpu-backends.md](gpu-backends.md) expected the packed `surf` code to have to name
*(instance, leaf)*, changing the encoding that is the largest single speed-up in this renderer's
history. It did not. The walk that chooses an instance is the walk that folds its span list in, so
it can simply say which one it chose: `Hit` gained an `int instance`, `resolve_sW` takes it, and
`packSurf`/`surfIn`/`surfOut` were not touched.

## What was found on the way

Two bugs, both in the **folded** path, both invisible to the unit tests that existed at the time.

**A folded group emitted the first member's geometry for every member.** Cornell's floor and
ceiling are both `6 x 0.2 x 8` and its left and right walls are both `0.2 x 6 x 8`, so each pair is
one shape with two placements. Under the threshold both are written out, and they were written out
from the *group's* tree, which is whichever root reached it first. The ceiling was drawn at the
floor's position and the right wall at the left wall's. A render found it, with two surfaces
missing and the metal sphere reflecting red where blue should be.

The fix is that a folded placement keeps and is emitted from its own tree. That also makes "below
the threshold, nothing changes" a property of the code rather than an argument about floating
point.

**The material slot pattern was masked out of the key.** A probe is emitted with no slot table, so
the primitive record's material field holds the *slot*, and the slot pattern is structural. Masking
it made `union { box(red) sphere(blue) }` and `union { box(red) sphere(red) }` compare equal, which
would hand the second appearance a slot list longer than its run of materials. Not visible in any
scene yet, and it would have been the moment one was written.

**What both point at.** The tests checked mechanisms and not outcomes: every test that exercised
more than one placement either used `translate:`, where the offset survives being emitted from the
wrong tree, or went through the shared path, which was correct. The process failure is worth naming
too: the before-and-after image comparison was run while cornell was still instanced, the threshold
arrived afterwards and changed which path cornell takes, and only the *timings* were re-measured.
Re-run the image comparison after any change to how a shape is reached, not only after changes to
what it emits.

The test that now covers this compiles every scene twice, at the shipped threshold and with
everything shared, reconstructs each leaf's world position from whichever tables that compilation
produced, and asserts the two sets agree. **Instancing may change how geometry is reached and never
where it is.** It does not care how a leaf is reached, so it catches a shape emitted from the wrong
tree, an instance matrix composed the wrong way round, a placement dropped, or a slot pattern
mismatched. It also caught its own first version, which composed the two matrices in the wrong
order.

## Where the code is

| Concern | File |
| --- | --- |
| Deciding which roots are the same shape | `Chroma.Core/Compilation/ShapeCanonicalizer.cs` |
| Shapes, appearances, the sharing threshold and the budget | `Chroma.Core/Compilation/ShapePartition.cs` |
| What a shape costs, and what a refusal says about it | `Chroma.Core/Compilation/ShapeCost.cs` |
| Counting statements as they are written, unrolling included | `Chroma.Core/Codegen/GlslWriter.cs` |
| The BVH, binned SAH, escape indices, one instance per leaf | `Chroma.Core/Compilation/InstanceBvh.cs` |
| Emitting a shape, and the probe that defines identity | `Chroma.Core/Codegen/GeometryEmitter.cs` |
| Splitting a scene too large for one program | `Chroma.Core/Compilation/SceneChunker.cs` |
| The tables every chunk shares, and why they must be shared | `Chroma.Core/Codegen/SceneTables.cs` |
| Running the stages, and the buffers they run over | `Chroma/Rendering/WavefrontRenderer.cs` |
| Table layouts the packer and the shader must agree on | `Chroma.Core/Compilation/GpuLayout.cs` |
| `PathState`, the stage functions, `Hit.instance`, `hitNormal` | `Chroma/Shaders/raytrace.glsl` |
| Uploading the two new tables | `Chroma/Rendering/SceneBuffers.cs` |
| Recompiling when the driver refuses, and naming which ceiling | `Chroma/Program.cs`, `Chroma/Rendering/GlCapabilities.cs` |
| Measuring what this driver will actually take | `tools/measure-shape-cost.ps1` |
| Tests | `tests/Chroma.Core.Tests/` — `InstancingTests.cs`, `ShapeCostTests.cs`, `ChunkingTests.cs` |

## What is left

### The ceiling still exists, on distinct shapes

Roughly 65,000 instructions divided by the cost of one shape body, which for a Bézier lathe is most
of a chess piece's budget and for a convex primitive is nearly nothing. A scene of two hundred
*different* turned pieces would still be refused, and no scene in the repository comes close.

`scenes/cube.chroma` was refused when this was written, and instancing could not help it: it is
8,000 boxes in nested `union`s, so it is one shape with 8,000 leaves rather than 8,000 placements of
one shape. It failed identically before this work.

**That has since been answered, and the answer was to make instancing able to see it.** A shape no
program can hold is now cut into the operands of its own `union` before the question "which roots
are the same shape" is asked, and what the cut exposes is that the twenty sub-cubes of `cube(3)` are
one shape standing in twenty places. The scene compiles at 3% of the budget. The concern below it,
that roots are unioned but not merged, is real and is why the cut declines to separate two
overlapping transmissive operands. See [cutting-unions.md](cutting-unions.md).

### Done since: a cost model and an honest refusal

The threshold above is a question about speed. Whether the program *fits* is a different question,
and until the compiler could answer it there was only one thing to do about a refusal: share
everything, and lose the folded form's speed on every shape in the scene to fix a problem caused by
two of them.

`Chroma.Core/Compilation/ShapeCost.cs` gives it a number. A shape's cost is counted as the emitter
writes it, so it is a property of what the shape emits in exactly the sense its identity is, and it
is reported by the same `Probe` that computes the signature — which means the number the partition
decides on is by construction the number the emitter will produce. `SceneCompiler` throws if the
two ever disagree.

Two things use it. `ShapePartition.Choose` shares what speed asked for and then, while the estimate
is over budget, sheds the repeated shape that saves the most until it fits; since sharing only ever
shrinks a program, this can add to what the threshold chose and never take any of it away, so a
scene that fits today is partitioned exactly as it was. And `GlCapabilities.ExplainOverflow` names
the three most expensive shapes with their source locations, or with the loop that generated them
when one did.

What a statement is, what was measured, and the model's error are in
[gpu-backends.md](gpu-backends.md). The short version is that **the budget is still a placeholder**.
The sweep found the weights wrong between shape kinds by about a factor of three — a scene of 111
prisms is refused where 55 lathes and 67 spheres compiles, though the model costs the second at
three times the first — so no number fitted to it would mean what it claims. The machinery is right
and the calibration is not, and those are separable: everything above works off whatever number the
budget holds.

### Done since: wavefront

The only approach that removes the ceiling rather than moving it. Geometry is split into chunks,
each under the budget, and ray state moves into buffers so that no single program holds the whole
scene. Instancing is what makes a chunk definable, since a chunk is a set of shapes and its own tree
over their placements.

It is not a second renderer. The path tracer is an explicit `PathState` and a set of stage
functions; the megakernel reads as `spawn; for (bounce) { intersect; shade; connect; }` over state
in registers, and the wavefront driver runs the same functions over state in a buffer.

Two details make it work rather than merely typecheck: the nearest-hit reduce is free, because
passes are sequential and each reads the current best `t`; and transmissive shadows compose, because
absorption along a segment is multiplicative and therefore order-independent.

`scenes/palisade.chroma` is what it was built for — two hundred hexagonal posts of two hundred
different sizes, which one program cannot hold and which now renders without a flag. It is this
phase's `chess-full`.

The parts:

- **The stages.** `spawnPath`, `intersectPath`, `shadeVertex`, `bouncePath`, `connectDirect`, over
  `PathState` and `Vertex`. `directLight` split into `sampleLight` and `lightWeight`;
  `shadowTransmittance` into a `ShadowRay` and `shadowStep`. The seams are at those two places and
  nowhere else, and the reason is the RNG: a wavefront defers the *trace* of a shadow ray but must
  not defer the *draw* that chose it, because the seed advances in a fixed order and moving one
  draw moves every random number after it.
- **The chunker.** `SceneChunker` packs shapes into chunks by cost, first-fit-decreasing, after
  `ShapePartition.Choose` has already shared everything it can — sharing costs a buffer read,
  splitting costs a whole extra pass, so sharing goes first. `SceneTables` gives every chunk one
  set of primitive, material and instance tables; only the BVH is per chunk.
- **The driver.** `WavefrontRenderer` compiles one program per chunk and five that carry no
  geometry, and runs the stages in order with a barrier between each. Compute only.

It was checked three ways and all three are byte-for-byte on the rendered PNG: the megakernel before
the stage split against after, the megakernel against the wavefront at one chunk, and one chunk
against two to six chunks at the same partition. Seventeen scenes each. The measured speed — it is
**faster** on the two heaviest scenes and slower on the light ones, which is not what was predicted
— is in [gpu-backends.md](gpu-backends.md).

A chunk cuts **between whole shapes** and never inside one, so `cube.chroma`, one shape with eight
thousand leaves, was still refused rather than split. That is no longer where the scene ends up: the
phase after this one cuts inside the union first, and what arrives here is four hundred appearances
of a shape of twenty, which needs no chunk at all. Chunking still cuts only between shapes, and
still exists for the scene that is over budget in aggregate rather than in any one shape. See
[cutting-unions.md](cutting-unions.md).

`P == 1` stays the megakernel and emits byte-for-byte what it emitted before, so no scene that
works today pays for any of this.

### Open questions, in the order they are worth answering

Kept here rather than in a tracker so that the next person to open this file sees them next to the
thing they are about.

- **The cost model's weights are wrong between kinds by ~3x.** The one that blocks a meaningful
  budget. Most likely suspects: how an unrolled lathe loop is counted against how a prism's contour
  test is. Until it is fixed, `ShapeCost.Budget` is a placeholder and every number derived from it
  is a placeholder too. See [gpu-backends.md](gpu-backends.md).
- **A small scene can hang the driver's compiler.** Thirty-two six-point lathes did not finish
  compiling in ten minutes. Not the instruction cap, not understood, and it is the kind of thing a
  sweep silently records as a capacity measurement. It now looks less like a hang: `chess-full`
  takes 159 s the first time it is compiled at a *quarter* of the budget, so compile time is
  steeply superlinear in something that is not line count, and fifteen minutes may be that curve
  further along. Re-measure it as a time before investigating it as a bug. See
  [gpu-backends.md](gpu-backends.md).
- **The sweep conflates every failure.** It should classify a refusal — instruction cap, register
  ceiling, timeout, driver reset — and refuse to bisect across a change of reason. It should also
  stop measuring cheap kinds on a base large enough to be the only thing measured.
- **Compaction.** Where a wavefront gets the rest of its speed: a per-bounce compacted index buffer
  built with atomics, so a dispatch covers live paths rather than every pixel. It is what would
  turn the current 1.25x loss on light scenes into a gain, and it should widen the 1.44x already
  measured on `chess-full`.
- **The wavefront's shadow record is 80 bytes whether or not the scene is transmissive.** An opaque
  scene reads neither the medium nor the running transmittance and could use 48, which is 40% of
  the largest buffer. One layout was kept because two are two things to get wrong, and the saving
  is written down rather than taken.
- **A chunked scene keeps every path buffer at full resolution.** About 180 MB at 1280x720 with one
  light, scaling with both. Tiling the frame would bound it regardless of resolution.
- **Chunks are packed by cost, not by position.** Packing spatially too would give each chunk a
  tighter tree and reject more rays. Measurable, unmeasured.
- **Cutting inside a top-level `union`. Done**, in the phase after this one, and it turned out not
  to be a chunking change at all: roots are already unioned separately, so a `union` root cut into
  one root per operand needs no new machinery, and what the cut buys is not more programs but the
  repetition instancing could not previously see. Both caveats written down here held. Two
  overlapping *transmissive* operands stop coalescing, so the cut declines that pair specifically;
  and the span width had to come down with the cut, so it keeps going until no cut shape is wider
  than `ShapeCost.MaxSpans`. `cube.chroma` is 3% of the budget and renders. See
  [cutting-unions.md](cutting-unions.md).
- **Unsharing under budget.** A scene above the threshold with room to spare could fold its cheap
  shapes back for speed. The Phase 1 measurement says the gain is the tree rather than the sharing,
  so this may be worth nothing; guessing risks the 5.8x.
- **Mirrored placements are not shared.** A negative determinant reverses surface orientation,
  which meets `Hit.flip` and the entering/leaving rule, and no scene exercises it. They work as
  singletons, which is exactly what they did before.
- **The sharing threshold is one number chosen from seven measurements.** It separates the two
  groups with room on either side, and it has not been swept.
- **The `--sdf` backend does not instance, and is not costed or chunked.** It is the demonstrator
  for a different question, and giving it a second axis to differ on would make neither comparison
  mean anything.

## See also

- [gpu-backends.md](gpu-backends.md): the ceiling, everything tried against it, and what each
  attempt measured.
- [code-generation.md](code-generation.md): why a scene is compiled to source at all.
- [performance.md](performance.md): the timing tables.
