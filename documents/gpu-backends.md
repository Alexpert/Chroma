# The instruction ceiling, and how the renderer reaches the GPU

> **Status: moved, by instancing.** `chess-full.chroma` compiles and renders. What the driver
> counts is now one body per **distinct** shape rather than one per root, so a scene is bounded by
> how much different geometry it holds and not by how much geometry it holds. The account of the
> ceiling and of everything that failed against it is kept below, because the negative results are
> the useful part and because the ceiling has been moved rather than removed.

Per-scene code generation ([code-generation.md](code-generation.md)) made every scene 2× to 17×
faster and removed the array-size limits that had silently truncated a lathe. It also introduced
a new limit, which this document is about: **a generated scene can be too large for the driver to
compile at all.**

The scene that found it is [`scenes/chess-full.chroma`](../scenes/chess-full.chroma), written on
purpose to go looking for edges. It did not compile with all thirty-two men on the board:

```
line 65886, column 1:  error: too many instructions
```

It was kept in the repository **because** it did not compile: it was the artifact that said where
the wall stood, and what any future work on instancing had to make pass. It now does, and it is
kept for the opposite reason.

That is NVIDIA's assembly profile `gp5fp` refusing a program past roughly 65,000 static
instructions. The number is not a Chroma constant, not an OpenGL constant, and not a document
anyone publishes — it is what this driver does.

This document records what the limit is, everything that was tried against it, what each attempt
measured, and what finally worked.

---

## What the driver actually counts

Not lines of GLSL. Not primitives. **Instructions in the flattened program**, after the driver
has done two things:

1. **Inlined every function call.** There is no `noinline` in GLSL and no way to ask for a real
   subroutine. A function called from thirty-two places is thirty-two copies of its body.
2. **Unrolled every loop whose bound is a compile-time constant.** A sixteen-iteration loop over
   a `const vec4 edges[16]` becomes sixteen copies of its body.

Both are usually what you want. Together, and applied to a shader whose whole design is "the
scene tree becomes nested calls with constants folded in", they mean the generated program's
*assembly* size is much larger than its source size, and grows faster.

This is why the line count on the console line is a rough guide and nothing more:

| Configuration of `chess-full` | Primitives | Generated lines | Result |
| --- | ---: | ---: | --- |
| 32 pieces + board, before body sharing | 162 | 10,514 | refused |
| 32 pieces + board, with body sharing | 162 | 7,434 | refused |
| 32 pieces, **board removed entirely** | 98 | 5,002 | **refused** |
| 20 pieces + board | 130 | 6,588 | refused |
| 16 pieces + board | 118 | 5,967 | compiles |
| `chess-half.chroma`, 16 men | 126 | 6,436 | compiles |
| 32 pieces + board, **instanced** | 32 | 3,342 | **compiles** |

The third row is the informative one and it is what pointed at the answer. Deleting all sixty-four
board squares, a third of the scene's primitives and a third of its source, still left a program
the driver would not take. The cost was in the turned pieces, whose lathe bodies unroll, and there
are only six *different* ones however many stand on the board.

**Roughly sixteen turned pieces was where the wall stood** on an RTX 4070 SUPER, driver 4.6. It now
stands at roughly sixteen turned pieces that are *different from each other*, which no chess set
is.

---

## The wall before this one

Worth recording because it was mistaken for the same wall for some time, and its fix is now load-
bearing throughout the emitter.

Before the pooling work, the same scene failed with several hundred of these instead:

```
error C5041: cannot locate suitable resource to bind variable "@list.2276-13065".
Possibly large array.
```

The cause: **the driver allocates storage per variable, not per live range.** Every root function
was inlined into `traceScene`, and every `SpanList_N` local in every root then existed
simultaneously as far as the register allocator was concerned. A queen's root alone declares
about 150 spans' worth of lists; a hundred roots asked for some two thousand at once. Worse, the
naming in the error message (`@list`, `@values`, `@crossings`, `@events`) showed that **array
function parameters** were the largest part of it: an `out SpanList_N list` parameter becomes a
fresh array at every inlined call site.

Four changes removed it, and they are why the emitter looks the way it does:

- **A pool of file-scope span-list globals**, allocated and released as the tree is walked, so
  what is declared is what is live *simultaneously in the deepest single root* rather than what
  the scene contains. Adding a hundred more pieces adds no slots. See `Take`/`Release` in
  [`GeometryEmitter.cs`](../src/Chroma.Core/Codegen/GeometryEmitter.cs).
- **No array parameters anywhere.** Operators are parameterless functions naming the globals they
  read and write (`union_s16_0_s2_1_s18_0`), which also dedupes them hard — every rook in a chess
  set shares one set of operators, because they land in the same pool slots.
- **Shared leaf scratch.** `gCross`, `gBreak`, `gDelta` and `gRoots` are one global each, sized to
  the hungriest leaf. A leaf owns its scratch for the length of one call and no two leaves are
  ever in flight at once, so one array of each kind is all a scene can want.
- **`PUSH` as a preprocessor macro** rather than a function, because it is called against a
  different list every time and a list parameter would cost an array per call site.

This worked completely: C5041 is gone, and the failure moved to the instruction count.

---

## What was tried against the instruction ceiling

### 1. Loops bounded by data rather than by constants

The insertion sorts were written `for (i = 1; i < N; ++i) { if (i >= count) break; ... }`, with
`N` a compile-time constant. The driver unrolls that to N² copies of the inner body — for a
sixteen-segment lathe, roughly three thousand instructions of pure waste, since an insertion
sort's inner loop is data-dependent and unrolling it buys nothing. The same shape appeared in the
operator sweeps, in `resolve`/`occludes`, and in the crossing-pairing loops.

All of them now bound on the data: `for (i = 1; i < count; ++i)`.

**Measured:** a real speed-up — `sweeps` 34.4 → 57.2 samples/s, `shapes` 44.5 → 62.7 (8 samples,
mid-iteration figures). **Did not move the ceiling.**

### 2. A newer OpenGL context, same fragment stage

The context request went from 3.3 to 4.6 and the tracer was compiled at `#version 460 core`.

**Measured:** refused at `line 65886` — *the same line as at 330*. The GLSL version does not
change which backend the driver lowers a fragment shader through.

### 3. A compute shader, storage buffers, in-place accumulation

The full modern path, and it is implemented and works: the tracer compiles as a compute shader
with `layout(local_size_x = 8, local_size_y = 8)`, the three scene tables become `std430` storage
buffers at explicit bindings, and the accumulation buffer becomes a single `rgba32f` image read
and written in place — no ping-pong, since one invocation owns one pixel.

**Measured, on the ceiling:** refused at `line 65887`. One instruction different from the
fragment stage. NVIDIA lowers both stages through the same assembly backend, so the cap is a
property of the driver rather than of the pipeline.

**Measured, on speed:** a wash, with one bad outlier. Thirteen scenes, 64 samples each:

| Scene | Compute | Fragment | |
| --- | ---: | ---: | ---: |
| primitives | 600.7 | 496.4 | 1.21× |
| csg | 477.0 | 436.3 | 1.09× |
| chess | 179.2 | 164.5 | 1.09× |
| chamber | 559.6 | 517.6 | 1.08× |
| magnify | 225.0 | 210.7 | 1.07× |
| fog | 94.8 | 92.8 | 1.02× |
| cornell | 341.1 | 347.5 | 0.98× |
| shapes | 297.7 | 312.1 | 0.95× |
| lattice | 85.6 | 90.5 | 0.95× |
| translucency | 218.4 | 233.1 | 0.94× |
| colonnade | 321.8 | 364.2 | 0.88× |
| glass | 274.6 | 347.6 | 0.79× |
| **sweeps** | **37.2** | **128.7** | **0.29×** |

`sweeps` has the widest root in the set at 24 spans — the heaviest register load — which points at
the compute profile allocating registers worse under pressure. It was not investigated further,
because the ceiling was the point and the ceiling did not move.

**Correctness was checked**, so the timings mean something: `csg` at 64 samples differs between
the two paths in 24 of 230,400 sampled pixels, worst channel delta 7/255 — last-bit differences in
the sub-pixel jitter along high-contrast edges, since one path interpolates the pixel centre
through the rasteriser and the other computes it from the invocation index.

The compute path is therefore **opt-in** (`--compute`) rather than the default. Shipping a default
that is 3.5× slower on a scene in the repository is not defensible on the strength of "it is the
newer API".

### 4. Storage buffers versus texture buffers

To find out whether the compute regression was the SSBO reads rather than the stage, `--tbo`
compiles the compute path against `samplerBuffer` instead. A compute shader can read either.

**Measured:** `sweeps` 35.7 with storage buffers, 35.6 with texture buffers. Not the buffers.

### 5. Sharing leaf bodies between identical solids

All sixteen pawns emit byte-for-byte identical geometry; only the `const mat4` that places them
differs. So the emitter now hashes each list-shaped leaf on its geometry and emits **one shared
body per distinct solid**, into a global of its own; each leaf keeps its matrix, calls the body,
and copies the answer into its pool slot. Convex primitives are excluded — their body is a single
`PUSH`, and routing that through a call and a list copy would cost more than emitting it twice.

**Measured:** source fell 10,514 → 7,434 lines, a 29% cut, at no runtime cost (`sweeps` 129.9,
`cornell` 401.8, unchanged within noise). The ceiling moved from about 115 primitives to about
118. **Essentially nothing.**

This is the most useful negative result here, and it follows directly from the first section:
**source-level sharing does not survive the inliner.** One body called from thirty-two places is
still thirty-two bodies in the assembly. Deduplicating text reduces compile time and the size of
the file you read with `--emit-shader`; it does not reduce what the driver counts.

The change was kept for those two reasons, not because it addresses the limit.

### 6. Making the scene cheaper

For completeness: dropping the Bézier tessellation from 3 steps per curve to 2 takes the full set
from 7,434 to 7,317 lines and is still refused. At 1 step the knight's two-curve profile stops
being a valid lathe. Deleting the board is the third row of the table above.

---

## Instancing, the thing that worked

Not deduplication of *text*, which is measured above and does not help, but putting the shared
body **inside a loop the driver cannot unroll**:

```glsl
while (node < uNodeCount) { ... }   // bound is a uniform
```

A loop whose bound the driver does not know expands its body exactly once, whatever the trip
count. That is the property being bought, and it is the only one that changes what the driver
counts. A `switch` on a runtime shape number inside it is a real branch rather than a copy, so the
program holds one body per **distinct** shape and the scene holds its placements in a buffer.

### The measurement

| Scene | Primitives | Shapes | Instances | Generated lines | Before |
| --- | ---: | ---: | ---: | ---: | --- |
| `chess-full` | 162 → **32** | **10** | 96 | 7,434 → **3,342** | refused |
| `chess-half` | 126 → **32** | **10** | 80 | 6,436 → **3,342** | compiled |
| `lattice` | 425 → **20** | **8** | 124 | 10,904 → **1,027** | compiled |

`chess-full` compiles and renders. Thirty-two pieces became six turned shapes and sixty-four
squares became two, which is what the third row of the table above says had to happen: the cost
was never in the count of solids, it was in the count of *different* lathes.

Speed, at 256 samples and 1280×720 on the same 4070 SUPER:

| Scene | Before | After | |
| --- | ---: | ---: | ---: |
| chess | 191.1 | 1106.7 | **5.79×** |
| lattice | 98.5 | 338.1 | **3.43×** |
| chess-half | 4.5 | 13.7 | **3.04×** |
| cornell | 643.7 | 673.7 | 1.05× |
| glass | 519.1 | 521.0 | 1.00× |
| colonnade | 664.7 | 662.9 | 1.00× |
| sweeps | 153.1 | 152.3 | 0.99× |

The gain is not the instancing, it is the **tree**. `traceScene` used to test every root's box
against every ray in source order; a scene of 125 lattice cells paid 125 box tests per ray per
bounce where it now pays about seven. Instancing is what makes a tree possible, since placements
have to be data before anything can sort them, and the tree is what pays.

### The price, stated plainly

- The world→local matrix stops being a folded `const mat4` and becomes four fetches, for repeated
  geometry only. A shape emitted once keeps its literal.
- Bounding boxes become per-instance data rather than per-root constants.
- Instancing is **not free at small scale**, and this was measured rather than assumed. Sharing
  everything shareable cost `glass` 35% and `cornell` 18%: a BVH walk is a loop of *dependent*
  memory reads where a run of folded guards is independent work the compiler can interleave. So a
  scene shares nothing until it has 32 repeated placements, and the driver overrides that
  threshold by refusing the program, since a scene that will not compile has no speed to protect.
  See `ShapePartition.DefaultShareFrom`.

One prediction in the previous version of this section was wrong and is worth recording. The
packed `surf` code was expected to have to name *(instance, leaf)*, changing the encoding that is
the largest single speed-up in this renderer's history. It did not: the walk that chooses an
instance is the walk that folds its span list in, so it can simply say which one it chose.
`Hit` gained an `int instance` and `packSurf`/`surfIn`/`surfOut` were not touched.

### What is left

The ceiling has moved, not gone. It now falls on **distinct** shapes: roughly 65,000 instructions
divided by the cost of one body, which for a Bézier lathe is most of a chess piece's budget and
for a convex primitive is nearly nothing. A scene of two hundred different turned pieces would
still be refused, and the refusal still names lines rather than the shapes that cost the most.

Two things follow, in increasing order of cost.

### SPIR-V

OpenGL 4.6 accepts SPIR-V directly (`glShaderBinary` + `glSpecializeShader`), and `Silk.NET.Shaderc`
can compile the assembled GLSL to it in-process. This bypasses the driver's GLSL front end.

**Not tried.** The expected value looks poor: the fragment and compute stages already share the
backend that imposes the cap, which suggests the cap lives below the front end rather than in it.
It is cheap enough to be worth one afternoon, and it is worth less now than it was: a scene has to
hold a great many *different* solids before the number matters at all.

### Wavefront rendering

The only approach that removes the ceiling rather than raising it. Ray state (origin, direction,
throughput, RNG) moves into buffers, and each bounce runs one intersection pass per chunk of
geometry. Instancing is what makes a chunk definable, since a chunk is a set of shapes and its own
tree over their placements. Arbitrarily large scenes, because no single program has to hold the
whole scene.

It is a restructuring of the path tracer into explicit stages rather than a second renderer: the
megakernel becomes `spawn; for (bounce) { intersect; shade; connect; }` over a `PathState` held in
registers, and the wavefront driver runs the same stage functions over a `PathState` held in a
buffer. On the OpenGL 3.3 fallback there are no storage buffers and no `imageStore`, so it would be
unavailable there; and every scene that fits in one chunk should keep the megakernel, which is all
of them today.

---

## How the two paths are built

One shader body, two stages. [`raytrace.glsl`](../src/Chroma/Shaders/raytrace.glsl) is compiled as
either a fragment or a compute shader, and the difference between them is three accessor macros
and two eight-line `main` functions. Everything else — the primitive maths, the polynomial solvers,
the span operators, the path tracer — is written once and reads the same either way. Two copies of
a path tracer would be two path tracers to keep correct.

| | Fragment (default) | Compute (`--compute`) |
| --- | --- | --- |
| Requires | OpenGL 3.3 | OpenGL 4.3 |
| Scene tables | `samplerBuffer` + `texelFetch` | `std430` storage buffers |
| Accumulation | two textures, ping-ponged | one `rgba32f` image, in place |
| Driven by | a fullscreen quad | `glDispatchCompute`, 8×8 groups |
| Entry point | `gl_FragCoord`, `FragColor` | `gl_GlobalInvocationID`, `imageStore` |

The accessors are `PRIMITIVE(i)`, `MATERIAL(i)` and `SHAPE(i)`, switched on `CHROMA_STORAGE_BUFFERS`
— which is deliberately a separate symbol from `CHROMA_COMPUTE`, because a compute shader can read
either and which is faster is a measurement rather than a deduction (see section 4).

The `#version` line is rewritten by the host from the detected tier, so the file on disk stays a
valid GLSL 330 fragment shader that a validator or an editor can open on its own.

### Choosing a path

[`GlCapabilities`](../src/Chroma/Rendering/GlCapabilities.cs) asks for a 4.6 core context and reads
back what actually arrived — a driver may return something newer than requested but never
something older. It reports the tier on the console line:

```
OpenGL 4.6 on NVIDIA GeForce RTX 4070 SUPER/PCIe/SSE2 -- fragment shader, texture buffers
```

The GL 3.3 fallback is **best-effort**: it renders everything it rendered before this work, and a
scene past its budget fails with a message that names the limit rather than dumping the driver's
assembly listing.

| Flag | Effect |
| --- | --- |
| *(none)* | Fragment shader, texture buffers. The measured default. |
| `--compute` | Compute shader and storage buffers, where the machine allows it. |
| `--tbo` | Compute shader, but reading the scene tables through a sampler. A/B lever. |
| `--emit-shader <path>` | Writes exactly what the driver is handed, at the tier's `#version`. |

---

## The other limit: how long one frame may take

Everything above is about how *large* a program the driver will compile. There is a second limit
with nothing to do with it, found while measuring the distance-field backend, and it is worth
knowing because it fails far worse.

The operating system stops any GPU command that has not returned in about **two seconds**. Windows
calls it Timeout Detection and Recovery. When it fires the driver is restarted and every buffer,
texture and program the renderer created stops existing.

**The failure has no message and no recoverable form.** The process is terminated with a fatal
native abort, `0xC0000409`, which is not a managed exception: no `catch` runs, no handler runs, and
nothing gets to print. `scenes/chess-full.chroma` under `--sdf` at 640x400 reproduces it exactly,
and the whole of what a reader sees is the two startup lines and then nothing.

What was done about it, in the order the three cover progressively worse cases:

| Mechanism | Catches |
| --- | --- |
| `glGetGraphicsResetStatus`, polled once per frame | a reset the driver survives and is willing to report |
| A guarded catch around the render loop | a reset that surfaces as a managed exception |
| A note before the first frame, and a warning after it | the fatal case, which reaches neither of the above |

**The first two are best effort and the third is the one that works.** A driver need only answer
the reset query when the context was created asking to be told, and Silk.NET's `ContextFlags`
exposes no way to ask; and the abort that kills the process is by construction uncatchable. So the
useful warning is the one printed *before* the dangerous frame, plus the measured one after the
first frame survives:

```
warning: the first frame took 2.1 s at 480x300. The operating system
         stops a GPU command at about two seconds and restarts the driver, which ends
         this program without a message. Halve --size to halve the frame.
```

That number is real: `chess-full.chroma` under `--sdf` at 480x300 sits at 2.1 s per frame, which is
already inside the danger band, and 640x400 is past it.

Two things follow for anyone reading a slow render. Frame time scales with pixels almost exactly,
so halving `--size` halves it. And the interactive path is the exposed one: a batch run draws the
same frames but nobody is waiting for a window to repaint, so a scene that is merely slow stays
merely slow.

## What a scene author should take from this

- A scene is bounded by **how much distinct geometry it contains**, not by how much geometry it
  contains. Sixty-four boxes are nearly free; sixteen *different* Bézier lathes are most of the
  budget.
- **Writing the same piece twice is free**, and this is the sentence that changed. The compiler
  works out which roots are the same solid standing somewhere else, so a chess set costs six
  pieces and a forest costs one tree. Nothing has to be said in the scene file for this to happen,
  and there is no syntax for it: the language did not change.
- Two solids count as the same shape when they emit the same GLSL, which is a stricter test than
  looking alike. A piece scaled to 90% is a second shape; the same piece rotated and moved is not,
  wherever the rotation and the move were written. What separates them is exact, so it never
  *almost* shares.
- Coarser tessellation helps a little and quickly stops helping.
- The console line prints shapes, instances, generated lines and the widest root. **Shapes is the
  number to watch when a scene is large**, because it is what the driver compiles, and the widest
  root when a scene is deep.

## See also

- [code-generation.md](code-generation.md) — why the scene is compiled to source at all, and what
  the generated code looks like.
- [performance.md](performance.md) — the timing tables, including the two paths side by side.
- [architecture.md](architecture.md) — the OpenGL 3.3 decision this turns into a tier.
- [instancing.md](instancing.md) — how shape identity is recovered without the language saying so,
  what it bought, and what is left to do.
