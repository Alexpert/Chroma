# Per-scene code generation

> **Status: implemented, first pass.** The tape interpreter is gone; every scene in `scenes/`
> compiles to its own shader, links, and renders. The measurements are in
> [performance.md](performance.md): between **2.1x and 17.1x**, with every image unchanged.
> What is not done yet is deduplicating structurally identical roots (step 6) and the shading
> `#define` table (step 7); both are described below in the future tense and marked.

## What changes, in one sentence

The scene stops being **data interpreted by a generic shader** and becomes **GLSL emitted for
that scene**, with one hard boundary: only the *geometry* is generated. The path tracer —
sampling, BRDF, lights, media, accumulation, tone mapping — stays a hand-written file.

## Why the iteration-0 decision was reversed

[architecture.md](architecture.md) rejected generated GLSL in iteration 0, and the reasoning
was sound at the time:

> At the scene sizes reached so far the performance difference is not measurable, while the
> debugging difference is enormous: a bug in a hand-written shader is a bug you can read.

Both halves of that have changed. The performance difference is now measurable and it is not
small; and the debugging cost is much lower than it looked, because the part of the shader
where the hard bugs actually live is not the part being generated.

### The wall is register pressure, and it is structural

`raytrace.frag` is compiled once, for every scene anyone might write, so **every array in it
is sized for the worst case**:

| Array | Size | Paid by |
| --- | --- | --- |
| `SpanList stack[MAX_STACK]`, `Span items[MAX_SPANS]` | 4 × 8 spans | every scene, including a scene of two spheres |
| `float crossings[MAX_CROSSINGS]` (prism, lathe) | 32 | every scene, including one with no prism and no lathe |
| `float events[24]`, `int deltas[24]` (sphere sweep) | 24 + 24 | ditto |
| `float breaks[16]`, `float crossings[16]` (blob) | 16 + 16 | ditto |

None of this is dead-code elimination the driver can perform: `stack` is indexed dynamically
(`stack[sp]`, `stack[sp - 2]`), which forces it into local memory, and the primitive
functions are all reachable through `primitiveSpans`'s ten-way dispatch on a runtime `kind`.

The consequence is measured and documented in [performance.md](performance.md): raising
`MAX_SPANS` from 8 to 9 costs 8% of the sample rate, and 9 to 10 **stops the shader linking
at all** on a 4070 SUPER. Iteration 11 found the same wall from the other side — adding one
bounding-box branch that `fog.chroma` never executed cost that scene 2.3×, purely because the
untaken side still held registers.

So the ceiling is not a tuning problem. It is what a single shader compiled for all scenes
costs, and it is why the three `CHROMA_*` `#define` switches exist already: they are a small,
proven, per-scene specialisation mechanism, and this iteration is that idea taken to its
conclusion.

### The lathe has two silent truncations, and both come from the ceiling

The renderer's standing rule is that it never truncates silently. The lathe breaks it twice,
and both breaks are forced by the shared array sizes.

**1. `MAX_CROSSINGS = 32` is not a bound for a lathe.** `latheSpans` pushes up to **two**
crossings per segment — every frustum band can be entered and exited by one ray. But
`PointList.Validate` compares a *segment* count against the *crossing* array:

```csharp
if (points.Count > GpuLayout.MaxCrossings) { /* reject */ }
```

`scenes/sweeps.chroma` is 3 Bézier curves × 8 steps = 24 segments, so up to **48 crossings
into a 32-slot array**. The shader then hits `if (count >= MAX_CROSSINGS) break;` and drops
the rest. Dropping a crossing flips the parity of every crossing after it, and `pairCrossings`
drops the unpaired tail — which renders as a solid with a slice missing, or turned inside
out. It was a live defect, not a hypothetical.

A prism is unaffected: a ray crosses each extruded wall at most once, so `points ≤ 32` is
exact there. The defect is the *conflation of the two into one constant*, which only exists
because there is one array shared by every scene.

**2. `SpansFor(Lathe, n) = Clamp(n, 1, 8)`** is documented in `GpuLayout` as a deliberate
clamp rather than a bound, with the reason stated plainly: the exact bound counts segments,
and a vase resolves to one or two spans whether it is drawn with 6 segments or 60, so holding
the exact bound would mean either visibly faceted curves or a `MAX_SPANS` the hardware
refuses to link. A 24-segment lathe therefore declares 8 spans and `push` drops the ninth.

Neither truncation survives per-scene generation, because there is no longer a shared array
to overflow: a lathe's crossing array is sized for *that lathe*.

### What is actually being bought

The motivating case is concrete. `scenes/chess.chroma` builds a rook from three stacked
cylinders, with the comment "a drum, a waist and a crenellated top" — because a lathe-turned
bishop or queen, which is exactly the shape a lathe exists for, cannot be expressed within
the 32-point ceiling. After this change it can, and that scene is the acceptance test.

## The governing rule

> **Constants for what is structural, buffers for what is repeated.**

A leaf's kind, its parameters and its shape-local transform cannot change without recompiling
the scene, so they become compile-time constants in the generated source. A root's placement
in the world repeats across instances of the same shape, so it stays in a buffer.

| Before | After |
| --- | --- |
| `uTape` — post-order opcodes | **gone** — the tree is nested calls over named locals |
| `uShapes` — guard boxes | **gone** — a guard is an `if` on two `vec3` literals |
| `uPrimitives` — kind, params, inverse matrix per leaf | **kept, for shading only** |
| `uShapes` — contour points, blob components, sweep spheres | **kept, for shading only** |
| `uMaterials` | **kept** unchanged |

The split between what is generated and what stays in a buffer is **the span path against the
shading path**, and it is not a compromise:

- **Spans are generated.** They are evaluated for every ray against every root, hundreds of
  times per pixel. Their transforms, tapers, thresholds and outlines are `const` in the source,
  so a leaf costs no fetch at all.
- **Normals are not.** A normal is recomputed **once per hit**, from whichever surface turned
  out to be visible. Generating a branch per leaf for it would grow the source with the scene —
  `lattice` would gain 425 blocks, each with its own `mat4` literal — to save one fetch per
  bounce. So `hitNormal` still reads the leaf record from `uPrimitives`, and a lathe's normal
  still walks its contour in `uShapes`, exactly as before.

The outline therefore exists twice: as a `const vec4[]` inside the leaf's span function, and as
texels for the normal. That duplication is cheap and deliberate, and it is the honest version of
"constants for what is structural, buffers for what is repeated".

## What the generated code looks like

A post-order tree becomes nested calls over **named locals**. The dynamically indexed
`stack[MAX_STACK]` disappears entirely, and with it the local-memory traffic that is the
single largest cost in the shader it replaces.

```glsl
// --- generated from scenes/chess.chroma ------------------------------------------
// shape 1 — rook(), chess.chroma:27

void shape1(vec3 ro, vec3 rd, out SpanList_2 result)
{
    // cylinder { base: [0, 0.12, 0], cap: [0, 0.30, 0], radius: base }   chess.chroma:31
    const mat4 M0 = mat4(/* world→local, folded and inverted at compile time */);
    SpanList_1 l0; leaf_cylinder(M0 * vec4(ro, 1.0), M0 * vec4(rd, 0.0), 0, l0);

    // cylinder { … }                                                    chess.chroma:32
    SpanList_1 l1; leaf_cylinder(/* … */, 1, l1);

    SpanList_2 u0; csgUnion_1_1_2(l0, l1, u0);

    // cylinder { … }                                                    chess.chroma:33
    SpanList_1 l2; leaf_cylinder(/* … */, 2, l2);
    csgUnion_2_1_2(u0, l2, result);
}
```

Three properties follow, and they are the entire justification for the rewrite:

- **No dynamic array indexing.** Every `SpanList` is a named local the register allocator can
  keep in registers.
- **Per-node sizing.** A leaf's list holds 1 span, not 8. A lathe's crossing array holds
  `2 × its own segment count`, not a global 32. Nothing is sized for a scene it is not in.
- **No kind dispatch.** `primitiveSpans`'s ten-way if-chain and `primitiveNormal`'s are gone;
  each call site names its function, and a scene of boxes is not compiled with a quartic
  solver.

### Sizing and emission order

Each node's span count comes from `SpanBudget`, which already computes exactly this and
survives the rewrite untouched: union adds, intersection takes the minimum, difference adds.
The emitter instantiates one `SpanList_N` struct per distinct `N` the scene uses, and one
`csgUnion_L_R_O` / `csgIntersection_…` / `csgDifference_…` per size triple it needs. GLSL 3.30
overloads on parameter type, so the call sites stay readable.

Operands are emitted **heavier first** (Sethi–Ullman order), which minimises how many
`SpanList`s are live simultaneously. This is the codegen analogue of the Strahler number
`SpanBudget.StackDepth` already computes for the interpreter's stack — the same quantity,
spent on register liveness instead of an array size.

### Bounding-box guards

Every root is guarded by its own box, and the guard is a plain `if` on two `vec3` literals:

```glsl
if (boundHit(ro, rd, vec3(-2.0, 0.0, -2.0), vec3(2.0, 3.4, 2.0), min(maxT, best.t)))
{
    SpanList_3 list;
    shape7(ro, rd, list);
    resolveRoot_3(list, best);
}
```

Under the interpreter a guard was an instruction with a jump target, patched after the subtree
was emitted, and it was worth having only above a hundred instructions — because *one* guard
anywhere turned `CHROMA_BOUNDS` on for the whole shader, and merely compiling that branch cost
`fog.chroma` 11%. Nothing about that trade-off survives: there is no shared branch to turn on,
so every scene gets guards and no scene pays for another's. A root holding a `plane` is
unbounded and simply gets no call.

### Deduplication — done, and it buys less than it looks like it should

Unrolling every root does not scale for ever: `chess.chroma` emits 3,278 lines from 69 roots
that are mostly the same four shapes repeated, and it is the scene that gains least (2.5x
against 12-17x elsewhere).

Half of this is now done, at the **leaf** level. All sixteen pawns of a chess set emit
byte-for-byte identical geometry; only the `const mat4` that places them differs. So the emitter
hashes each list-shaped leaf on its geometry and emits one shared body per distinct solid, into
a global of its own; each leaf keeps its matrix, calls the body, and copies the answer into its
pool slot. Convex primitives are excluded — their body is a single `PUSH`, and routing that
through a call and a list copy would cost more than emitting it twice.

It cut `chess-full` from 10,514 to 7,434 generated lines at no runtime cost. It did **not**
meaningfully raise the ceiling — about 115 primitives to about 118 — because the driver inlines
every call, so one body called from thirty-two places is still thirty-two bodies in the
assembly. Sharing text reduces compile time and the size of the file `--emit-shader` writes; it
does not reduce what the driver counts.

The other half, **instancing**, is done, and it is the part that actually worked: a loop whose
bound is a uniform expands its body exactly once, whatever the placement count. `chess-full` fell
from 7,434 generated lines to 3,342 and from a hundred-odd root bodies to ten shapes, and it
compiles. It cost the folded `const mat4` for repeated geometry only, since placement now comes
from a buffer, and it did **not** cost the packed `surf` encoding, which this paragraph expected
it to: the walk that chooses an instance is the walk that folds its span list in, so it can say
which one it chose, and `surf` still names a leaf. See [gpu-backends.md](gpu-backends.md).

The two results belong together, and they are the same lesson from opposite sides. Sharing a
**body** between identical solids does nothing, because the inliner puts the copies back. Sharing
a **placement** is everything, because a buffer is not something the driver counts.

### Surfaces and normals

The packed-`surf` encoding is kept exactly as it is. It is the largest single speed-up in the
renderer's history — 1.75× on `cornell`, 8.3× on `lattice` — and nothing here improves on it.
Only its meaning narrows: `surf` now names a **leaf within a shape**, and `Hit` gains an
`instance` field recorded by the resolve loop. `hitNormal` becomes a generated
`shapeNormal(int shape, int leaf, vec3 local)`, still called from exactly one place.

## Shading specialisation by `#define` — *partly done*

Geometry is generated; shading is **specialised by the preprocessor**, extending the
`CHROMA_TRANSMISSION` / `CHROMA_MEDIA` / `CHROMA_BOUNDS` mechanism that iteration 11 already
proved is worth up to a factor of two. A `ShadingProfile` computes the symbols from the
packed material table and the scene's lights — read off what was actually uploaded, the same
discipline as `CompiledScene.HasTransmission`, so the two cannot drift.

| Symbol | Derived from | What it removes |
| --- | --- | --- |
| `CHROMA_TRANSMISSION` | any material with transmission > 0 | the transmissive shadow walk — **in place** |
| `CHROMA_MEDIA` | any material with scattering > 0 | volume sampling along every segment — **in place** |
| `CHROMA_LIGHTS n` | exact light count | replaces `MAX_LIGHTS = 8`; shrinks four uniform arrays and the shadow loop |
| `CHROMA_POINT_LIGHTS`, `CHROMA_DIRECTIONAL_LIGHTS` | light kinds present | the `uLightKind` branch, when a scene uses only one kind |
| `CHROMA_AREA_LIGHTS` | any light with radius > 0 | sphere-light sampling, when every light is an idealised point |
| `CHROMA_EMISSIVE` | any material with non-zero emission | the emission gather |
| `CHROMA_BOUNCES n` | `RenderSettings.MaxBounces` | makes the bounce loop constant-bound and unrollable; `uMaxBounces` stops being a uniform |

Every symbol is declared `#ifndef` / `#define <default>` / `#endif`, so `raytrace.frag` still
compiles standalone with everything on — which is what keeps it openable by a tool that knows
nothing about the host, and what makes the A/B lever below possible.

Turning a symbol off may only remove a path the scene never takes, so a specialised render
must be **pixel-identical** to a generic one. That is the acceptance criterion for this half
of the work.

`CHROMA_BOUNDS` is gone from this table, and its absence is the point: it existed because one
guard anywhere cost every scene the branch. There is no shared branch left to gate.

## Reading a generated shader

This is the part [architecture.md](architecture.md) was right to worry about, and it is
answered in four ways.

1. **Only geometry is generated.** The ~2400 lines where the hard bugs live — importance
   sampling, the GGX BRDF, the transmissive shadow walk, the medium integral, the running
   average — remain a hand-written file that can be read, edited and diffed. The generated
   block is a few hundred lines of interval arithmetic whose correctness is checkable against
   `documents/csg-raytracing.md`.
2. **`--emit-shader <path>` writes the generated source to disk.** A driver error names a real
   file at a real line, and `Shader.Inject`'s existing `#line` fixup keeps the hand-written
   half's numbering pointing at `raytrace.frag`.
3. **Emission is deterministic.** The same scene emits byte-identical source, so two versions
   of the emitter can be diffed, and so can two scenes.
4. **Every generated function carries the source span it came from**, as in the example
   above — `chess.chroma:31`, not `leaf 7`.

## Limits that remain

The rewrite removes `MAX_SPANS`, `MAX_STACK`, `MAX_CROSSINGS`, `MAX_SWEEP_EVENTS` and
`MAX_BLOB_EVENTS` as global constants. What is still bounded, and why:

- **Evaluator budgets** (`MaxLoopIterations`, `MaxFunctionCalls`, `MaxCallDepth`) are
  unchanged. They stop a runaway scene before compilation, and they are the real backstop.
- **The driver's instruction ceiling** replaces `MaxInstructions` as the thing a huge scene runs
  into, and it has been measured rather than guessed: about 65,000 instructions in the flattened
  program. What the driver counts is instructions *after* it has inlined every call and unrolled
  every constant-bound loop, so generated line count is only a rough guide to it. Sharing a body
  between identical solids, which was tried, cuts the source by 29% and the ceiling by almost
  nothing, because the inliner puts the copies back.
  What did move it is **instancing**: the ceiling now falls on how many *different* shapes a scene
  holds rather than how many solids, because a repeated placement is a record in a buffer and a
  buffer is not something the driver counts. `scenes/chess-full.chroma`, kept in the repository
  precisely because it did not compile, does. The full account, including the compute and OpenGL
  4.6 attempts and what each measured, is in [gpu-backends.md](gpu-backends.md).
- **`MAX_SHADOW_STEPS`** stays a hand-written constant: it is a quality knob, not a capacity
  one.

## Risks taken knowingly

| Risk | How it turned out |
| --- | --- |
| Driver compile time per scene — the failure mode codegen was rejected for | Not a problem yet. `lattice` at 11,885 lines compiles inside the first frame, which was already excluded from benchmark timing because it always carried driver compilation. Dedup is the mitigation if it ever bites; `GL_ARB_get_program_binary` keyed on the source hash sits behind that. |
| Unrolled code is not automatically faster; more instructions can hurt occupancy | The claim was specifically about *removing the dynamically indexed span stack* and *right-sizing arrays*, not about unrolling, and the measurements bear it out: the smallest scene (`cornell`, 12.4x) and the largest (`lattice`, 14.9x) gain alike, which is what a state-bound rather than instruction-bound shader looks like. |
| Loss of debuggability | `--emit-shader` writes exactly what the driver is handed; the emitter annotates every function; output is deterministic, so two scenes can be diffed. This was used during the rewrite itself — the first link failure named a line in the emitted file, and the fix was obvious from reading it. |
| No interpreter to fall back to | Baselines were captured first and are in [performance.md](performance.md). The interpreter stays in git at `9fc89ee`. Both paths are deliberately not maintained side by side. |
| A uniform that vanishes | `Shader.GetUniformLocation` throws when a uniform is stripped, which is a deliberate and valuable property. It has not had to be weakened: `uPrimitives`, `uMaterials` and `uShapes` are all read by the shading path, which every scene has. |

## Step order

Verification during the iteration is deliberately light: each step checks that the generated
shader **compiles and links**, then renders a small selection of scenes at a low sample count
against the captured baseline. That is enough to attribute a regression to one emitter. The
exhaustive pass — every scene, converged images, the full timing table — happens once, at the
end of the iteration, and is what fills in the tables above.

1. ~~Capture baselines — timings and images for every scene in `scenes/`.~~ **Done**;
   [performance.md](performance.md).
2. ~~`GlslWriter`, `SpanLibrary`, `LeafEmitter`, `GeometryEmitter`; all ten primitives; the
   splice marker in raytrace.frag; the tape builder and its buffers deleted.~~ **Done.**
3. ~~Lathe and prism crossing arrays sized from their own outlines.~~ **Done.**
4. Dedup and instancing; compile time measured.
5. `ShadingProfile` and the rest of the `#define` table.
6. Acceptance: a lathe-turned bishop, queen and pawn in `scenes/chess.chroma`.

Dead code is removed as each step makes it dead, not collected into a cleanup step at the
end: the tape builder, `CompiledScene`, `GpuLayout`'s array-size constants and the
interpreter half of `raytrace.frag` go the moment nothing calls them. Two renderers half
present at once is the one state this rewrite cannot afford.

## See also

- [csg-raytracing.md](csg-raytracing.md) — the span algorithm and the per-primitive maths the
  emitters port. Unchanged by this work; only the *encoding* sections are superseded.
- [architecture.md](architecture.md) — the iteration-0 decision this document reverses, kept
  as the record of what was decided and why it was right then.
- [performance.md](performance.md) — the measurements quoted above, and the before/after
  tables this iteration adds.
- [gpu-backends.md](gpu-backends.md) — the limit this work introduced: how large a scene the
  driver will compile, everything tried against it, and how the fragment and compute paths are
  built from one shader body.
