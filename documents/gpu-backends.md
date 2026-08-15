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

The distance-field backend bounds its march the same way and for the same reason. `uMarchSteps`,
which `--march <n>` sets, is a uniform rather than a constant because a constant bound would be
unrolled into as many copies of the scene's whole field function as a ray may take steps. That the
bound is also a useful thing to turn is a second benefit, not the reason.

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

---

## Knowing the cost before the driver does

The ceiling has moved, not gone. It now falls on **distinct** shapes: roughly 65,000 instructions
divided by the cost of one body, which for a Bézier lathe is most of a chess piece's budget and
for a convex primitive is nearly nothing. Two things needed a number rather than a shrug.

The **partition** has to decide what to share before the driver has seen anything, and its only
rule was a count of placements, which is a question about speed and not about whether the program
fits. The **refusal** quoted a line count and advised "fewer distinct solids", and after
instancing neither is the useful thing to say: line count was measured not to predict this at all,
and a scene is over budget because of two or three shapes far more often than because of all of
them.

### What is counted

A **statement after unrolling**, counted by `GlslWriter.Cost` as the emitter writes. The cost of a
shape is therefore a property of what it emits, in the same sense that its identity is: there is
no second description of a solid to drift out of step with the one that generates the code, and an
eleventh primitive would be costed correctly without anyone adding a weight for it.

Three things about the count are worth stating, because two of them are counter-intuitive and all
three are load-bearing:

- **A leaf costs once per call, not once per body.** `LeafEmitter` shares one body between
  identical solids, which saves source and saves nothing at all here: the driver inlines every
  call, so a body written once and called sixteen times is sixteen copies in the assembly.
- **A loop bounded by a literal is multiplied by its trip count.** This is the entire difference
  between this and a line count. `for (int e = 0; e < 24; ++e)` over a `const vec4 edges[24]` is
  twenty-four copies of its body, and that is where a lathe's cost lives.
- **An operator costs a constant, not its span width.** Every loop in `union_*`, `intersect_*` and
  `complement_*` is bounded by a list's `count`, which is a runtime field, so the driver compiles
  the body once. An operator over two twenty-four-span lists costs exactly what one over two
  singles costs. A wide CSG tree is cheaper than its span counts suggest.

What is **not** claimed is that a statement is an instruction. One becomes anywhere between zero
and a dozen, and `PUSH` is a macro that counts as one and expands to five. The claim is only that
the total is proportional to what the driver counts, with the constant of proportionality
measured.

### Measuring the constant

The driver never says how many instructions it counted. It answers one bit at a time, by compiling
or refusing, so that is what `tools/measure-shape-cost.ps1` asks it. For each shape kind it
generates a scene of N shapes that are **different from each other** — a repeated shape is shared
and costs nothing extra, which is the whole point of instancing and would measure nothing here —
brackets by doubling, and binary searches the largest N that compiles.

Each kind then gives a bracket on the driver's capacity: the estimate at the largest N that
compiled, and the estimate at the smallest N that did not. If the model weighed every kind on the
same scale, every bracket would contain one number. How far they are from doing so is its error.

**They do not, and the error is about a factor of three.** That is the result, and the rest of this
section is what it is made of.

#### What the sweep measured

On an RTX 4070 SUPER, OpenGL 4.6, fragment path.

| Case | Compiles at | Refused at | How it was refused |
| --- | ---: | ---: | --- |
| prism, 6 edges | 13,431 | 13,552 | too many instructions |
| prism, 12 edges | 17,370 | 17,563 | too many instructions |
| sphere † | 42,103 | 42,142 | too many instructions |
| box † | 42,259 | 42,298 | too many instructions |
| cylinder † | 41,440 | 41,479 | too many instructions |
| cone † | 40,660 | 40,699 | too many instructions |
| torus † | 40,525 | 40,570 | too many instructions |
| `difference { box sphere }` † | 41,939 | 42,018 | too many instructions |

† **Measured on a base of 55 twelve-segment lathes, and therefore not independent.** Those six
numbers are within 4% of each other and that agreement is an artifact, not a result: the base is
39,490 statements of the 42,000, so every one of them mostly measured the base. Reading them as
six agreeing measurements — which is what the first draft of this section did — is reading one
measurement six times.

The two cases with no base are the informative ones, and they are refused at **a third** of what
the based cases reach. A scene of 111 prisms will not compile where 55 lathes and 67 spheres will,
though the model costs the second at three times the first. So the weights are wrong **between
kinds**: the model over-costs a lathe, or under-costs a prism, by something close to an order of
magnitude relative to what the driver charges.

The base was there for a reason — four thousand spheres take minutes to refuse and say the same
thing — and the reason is still true. What is now clear is that the cure was worse: a base large
enough to make a cheap kind measurable is large enough to be the only thing measured. A future
sweep wanting cheap kinds needs a base that is *small* relative to the capacity, and to accept
that some kinds are simply expensive to measure.

#### Two ceilings, not one

Which one a scene meets is a property of what it is made of rather than of how far over it is.

| Driver message | Reached by |
| --- | --- |
| `error: too many instructions`, against an assembly line number | 112 prisms |
| `error C5041: cannot locate suitable resource to bind variable`, one line per temporary | 200 prisms |
| `fatal error C9999: *** exception during compilation ***` | `scenes/cube.chroma`, some twenty times over |

C5041 is the register ceiling rather than the instruction one, and it is the same failure iteration
7 met from the other side when every span list was a local. `GlCapabilities.IsOverflow` did not
recognise it until this sweep produced one, which meant a scene refused that way skipped the retry
entirely and showed the reader two hundred lines of driver log. It is recognised now, and
`ExplainOverflow` names whichever ceiling it was, because sending an author to count instructions
when the driver ran out of registers would be sending them to the wrong place.

The assembly line number in the first message is **not** an instruction count. It moved by 144 for
sixteen more prisms while the program grew by 1,936 statements, so it names a position in a
listing and not a total. There is still no way to ask the driver what it counted.

#### Open, and worth knowing before trusting a budget

- **The weights are wrong between kinds by ~3x.** `ShapeCost.Budget` is still a placeholder for
  this reason: a number fitted to the lowest measured capacity would be safe and would also chunk
  scenes that do not need it, and a number fitted to the average would refuse to protect the ones
  that do. Finding out *which* weight is wrong — most likely how an unrolled lathe loop is counted
  against how a prism's contour test is — is the work that makes a budget meaningful.
- **A small scene can wedge the driver's compiler.** Thirty-two six-point lathes — 32 primitives,
  12,224 estimated statements, less than a third of what compiles elsewhere — did not finish in
  **fifteen minutes and 1,707 seconds of CPU**, and was killed rather than refused. Sixteen of the
  same lathes compile normally. Whatever this is, it is not the instruction cap, and the sweep was
  scoring it as a refusal by timeout: which is how a compiler bug gets quietly recorded as a
  capacity measurement.
- **The sweep conflates every non-zero exit.** It should record *why* a trial failed — instruction
  cap, register ceiling, timeout, driver reset — and refuse to bisect across a change of reason.
  Its current output cannot distinguish a capacity boundary from a compiler bug.

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
megakernel is `spawn; for (bounce) { intersect; shade; connect; }` over a `PathState` held in
registers, and the wavefront driver runs the same stage functions over a `PathState` held in a
buffer. On the OpenGL 3.3 fallback there are no storage buffers and no `imageStore`, so it is
unavailable there; and every scene that fits in one chunk keeps the megakernel, which is all of
them today.

**Built.** The pieces:

- `raytrace.glsl` holds the stages — `spawnPath`, `intersectPath`, `shadeVertex`, `bouncePath`,
  `connectDirect` — over an explicit `PathState` and `Vertex`. `directLight` and
  `shadowTransmittance` are split at the two seams a wavefront needs, `lightWeight` and
  `shadowStep`. The megakernel is their composition.
- `SceneChunker` splits a partition into chunks that each fit a budget, `SceneTables` gives every
  chunk one shared set of tables, and each chunk carries its own BVH at its own `NodeBase`. A
  scene of one chunk emits byte-for-byte the text it emitted before any of this existed.
- `WavefrontRenderer` compiles one program per chunk plus five that carry no geometry, and runs
  `spawn; for (bounce) { intersect x P; shade; shadow; connect } gather`.

The seam that decides whether the split is sound is **index identity across chunks**: a leaf's
number is a literal in generated code, a shape's id travels in an instance record, and both have to
mean the same thing in every program that reads the shared tables. Getting one wrong renders the
wrong solid rather than failing to compile, which is why `ChunkingTests` is mostly about indices.

Two properties make the pass sequence correct rather than merely typed. The nearest-hit reduce over
chunks is free, because the passes are sequential and each is handed the current best `t` as its
`maxT` — `traceScene` already took that parameter. And transmissive shadows compose, because
absorption along a segment is multiplicative and therefore order-independent.

Only the intersect stage carries geometry, and it serves the shadow wave too, switched by `uWave`
around **one** `traceScene` call site. Two call sites would be two inlined copies of the scene walk
in one program, which is the C5041 failure above met deliberately; `traceScene` takes `anyHit` as a
value rather than having a wrapper per question for exactly this reason, and that decision, made
for iteration 7, is what makes one program per chunk enough instead of two.

#### What it was checked against

Three comparisons, all at 96x64 and 8 samples, all **byte-for-byte on the PNG**:

| Comparison | Result |
| --- | --- |
| Megakernel before the stage split against after | 17 of 17 scenes identical |
| Megakernel against the wavefront forced on at one chunk | 17 of 17 scenes identical |
| One chunk against 2–6 chunks, at the same partition | 17 of 17 scenes identical |

The third needs the partition held still to mean anything, since a budget low enough to force
chunking also makes `ShapePartition.Choose` share more, and sharing legitimately changes the last
bits. Comparing at the fully-shared estimate against a quarter of it holds the partition fixed and
varies only the number of programs. `--budget` exists for that comparison and for nothing else.

Byte-identity across *different programs* was not expected — the plan allowed for a stated
tolerance — and it is worth knowing why it holds: the stages compute the same expressions in the
same order, and the RNG seed crosses the buffer bit-cast rather than converted, so the sequence of
random numbers is the same one. It is also what caught the one real bug in the shadow walk, where
`inMedium` was reconstructed each step instead of carried. That is invisible in vacuum, since
`exp(-0 t)` is 1 however often it is applied, and wrong by a mean of 12/255 across three quarters
of the frame inside fog. Three scenes differed, they were exactly the three with `CHROMA_MEDIA 1`,
and that was the whole diagnosis.

#### What it costs, measured

One sample becomes `1 + B(2P + 2)` dispatches for an opaque scene against the megakernel's one, and
there is no compaction, so a dead path still occupies an invocation. The prediction written here
before measuring was that this would be **slower on every scene that fits in one program**. That
was wrong, and interestingly so. At 480x300 and 64 samples, in samples per second:

| Scene | Megakernel | Wavefront | |
| --- | ---: | ---: | --- |
| chess-full | 13.9 | **20.0** | 1.44x faster |
| sweeps | 161 | **189** | 1.17x faster |
| cornell | 560 | 447 | 1.25x slower |
| lattice | 456 | 362 | 1.26x slower |
| glass | 621 | 426 | 1.46x slower |

The two that gain are the two heaviest programs, and both were already known to be **register**
bound rather than instruction bound: `chess-full` is the largest shader in the repository, and
`sweeps` is the scene recorded above as 3.5x *slower* on the compute path because its 24-span root
is the heaviest register load in the set. Splitting the path into stages cuts what any one kernel
holds live, which is the classic reason a wavefront helps and which this happens to demonstrate on
the only two scenes heavy enough to show it. The light scenes pay the dispatch overhead and get
nothing back, which is the expected half.

So the rule is not "the wavefront is slower". It is that the wavefront trades dispatch count for
occupancy, and which way that goes depends on whether a scene was register bound to begin with —
which is the same property that decides whether it hits C5041 rather than the instruction cap. The
scenes that need chunking are by construction on the heavy end of that.

`scenes/palisade.chroma` is the acceptance artifact: two hundred hexagonal posts of two hundred
different sizes, refused as one program with C5041, rendered as two chunks without a flag.

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
| `--wavefront` | Forces the wavefront on a scene that does not need it. Implies `--compute`. Automatic when the scene had to be split, so it is a comparison lever rather than a way to render. |
| `--budget <n>` | Overrides what one program may weigh, forcing a scene to be split. The only way to compare a chunked render against an unchunked one, since a scene that genuinely needs chunking has nothing to compare against. |
| `--emit-shader <path>` | Writes exactly what the driver is handed, at the tier's `#version`. |

---

## What a compile costs in wall time

Everything above measures how *large* a program the driver will take. How *long* it takes to answer
is a separate quantity, it is not predictable from the first, and it is the one a reader actually
waits on. Measured on an RTX 4070 SUPER, driver-side time being the gap between the capabilities
line and the first thing printed after the program comes back:

| Scene | Estimated | Generated | This program | The driver | Second run |
| --- | --- | --- | --- | --- | --- |
| `cornell.chroma` | 1% | 406 lines | 0.07 s | 0.1 s | 0.1 s |
| `chess-full.chroma` | 24% | 3,342 lines | 0.10 s | **159 s** | **0.5 s** |
| `palisade.chroma` | 121% | 17,570 lines | 0.09 s | 13.8 s | 0.15 s |
| `cube.chroma` | 1360% | 157,628 lines | 0.55 s | **149 s**, then 134 s | **no relief** |

The `cube.chroma` row is history. It is what the scene cost while it was compiled as one shape of
eight thousand leaves; it is now cut into four hundred appearances of a shape of twenty, comes to
3% of the budget and 1,626 lines, and compiles in about a second. The row is kept because the
lesson in it is not about that scene. See [cutting-unions.md](cutting-unions.md).

Four things come out of that table and none of them was obvious.

**Almost none of the wait is this program.** Parsing `cube.chroma`, recovering its shapes and
generating its 157,628 lines takes 0.55 s of the 150. Every attempt to make the wait shorter has to
be aimed at the driver or at not calling it.

**Time is not proportional to size.** `chess-full` is at a *quarter* of the instruction budget with
3,342 generated lines and takes 159 s the first time; `palisade`, five times the code and over the
budget, takes 13.8 s. Whatever the compiler's cost is superlinear in, it is not line count, and a
scene can be nowhere near the ceiling and still take minutes.

**A driver caches what it compiled and never what it refused.** That is the whole difference
between the two slow rows. `chess-full` costs 159 s once in the life of the machine and 0.5 s
thereafter; `cube.chroma` was refused, so nothing was stored, and it paid 149 s, then 134 s, then
again on every run forever. The retry in `Program.CompileTracer` is bounded at three attempts on the
strength of a remark that said "a near-ceiling program takes seconds to be refused". It is minutes,
and the bound matters much more than it was thought to.

**The scene that "wedges the compiler" is probably this curve, further along.** The open question
above records thirty-two six-point lathes at 12,224 statements failing to finish in fifteen minutes.
Against a `chess-full` that takes 159 s at 4,923 statements, a hang is no longer the only
explanation. Nobody has re-measured it, so it stays open; but it should be re-measured as a time
before it is investigated as a bug.

### Several programs at once

A chunked scene compiles one program per chunk plus the five wavefront stages that carry no
geometry. Those are independent (no program reads anything another produces) and were nonetheless
compiled strictly one after another. Fixing that needed **three** things to be true at once, which
is why partial attempts measured nothing:

1. every stage handed over before anything is linked, because a link is where the driver must have
   the shader it is linking;
2. every program linked before anything is asked about, because asking is the only thing that
   waits;
3. `glMaxShaderCompilerThreadsARB` called.

The third is the one that is not in any tutorial and is worth stating plainly. `GL_ARB_parallel_
shader_compile` says `MAX_SHADER_COMPILER_THREADS_ARB` **starts at the implementation maximum**,
which reads as though the driver is already using every thread it has. On this driver it is not:
with the extension present, `GL_COMPLETION_STATUS_ARB` answering, and points 1 and 2 both done, a
`palisade` forced to ten chunks still compiled its fifteen programs end to end.

| Programs | One after another | Together | |
| --- | --- | --- | --- |
| 15 (10 chunks) | 11.1 s | **3.6 s** | 3.1x |
| 17 / 18 (12-13 chunks) | 9.7 s | **3.5 s** | 2.8x |
| 19 / 20 (14-15 chunks) | 8.6 s | **3.3 s** | 2.6x |

Measured by interleaving two builds over a ladder of `--budget` values, one build per rung, because
the driver's cache makes a straight repeat meaningless: each budget produces a program text the
cache has never seen, and alternating the builds down the ladder keeps the trend in chunk size from
landing on one of them. The pairs are adjacent rungs and not the same program, which is why the
matched-chunk-count row is the one to read.

**It is worth nothing on a warm cache**, where each program comes back in a millisecond or two, and
**nothing at all on a scene with one program**, which is every scene that is not chunked. In
particular it does nothing for `cube.chroma`: one program's compile is inside the driver, on one
thread, and is not ours to divide.

### What is said while it happens

A step that outlasts one second counts itself out on stderr and takes the line back when it
finishes: the scene compile, the driver compile, each of a wavefront's programs as it comes back,
and which of the three retry attempts is running. The grace period is what keeps every fast scene
and all sixty of the manual's renders looking exactly as they did, and stderr is chosen so that
stdout stays the channel a script reads. Where the output is redirected there is no cursor to move,
so the count is repeated as plain lines every fifteen seconds instead of repainted.

**The estimate is still not allowed to refuse a scene.** It was tempting on `cube.chroma`, which
was knowably hopeless before the driver was called: 1360% of the budget, one shape accounting for
all of it, and so neither sharing nor splitting able to help. Refusing it would have turned 135 s
into 0.6 s. It was handed over anyway, because the driver is the authority and the cost model is
wrong between shape kinds by about 3x, and that turned out to be the right call for a reason nobody
argued at the time: **the scene was not hopeless, the compiler was.** Cutting inside the top-level
`union` takes it to 3% of the budget, and an estimate allowed to refuse would have made that harder
to find rather than easier. See [cutting-unions.md](cutting-unions.md).

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
so halving `--size` halves it, and on the distance-field backend halving `--march` does much the
same at the cost of whatever converges slowly, which is why the reset message names both levers and
prints the halved value for each. And the interactive path is the exposed one: a batch run draws the
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
- The console line prints shapes, instances, generated lines, the widest root, and **what share of
  the instruction budget the scene is estimated to spend**. That last number is the one to watch
  when a scene is large: it is visible before a scene hits the wall rather than only in the message
  that says it did. Shapes is what the driver compiles; the widest root matters when a scene is
  deep.
- A scene the driver refuses is now told which shapes cost the most, where they were written, and
  which loop generated them. The advice that comes with it is the true one: fewer shapes that are
  *different from each other*.

## See also

- [code-generation.md](code-generation.md) — why the scene is compiled to source at all, and what
  the generated code looks like.
- [performance.md](performance.md) — the timing tables, including the two paths side by side.
- [architecture.md](architecture.md) — the OpenGL 3.3 decision this turns into a tier.
- [instancing.md](instancing.md) — how shape identity is recovered without the language saying so,
  what it bought, and what is left to do.
