# Performance: what was tried, and what it actually bought

Iteration 11's subject is render time at an unchanged image. This is the record of every
optimisation attempted, with the measured gain of each — including the four that were
implemented, measured, and taken back out.

The rule the iteration set for itself:

> An optimisation is admissible if the image it converges to is the image it converged to
> before.

That is testable rather than a sentiment, and it was tested the strongest way available: every
scene in the repository was rendered before and after and compared **byte for byte**. All
eleven are identical. Nothing below trades correctness for speed.

---

## The result

Measured on a GeForce RTX 4070 SUPER at 1280×720, against the commit that closed iteration 10.

| Scene | Samples | Before | After | Gain |
| --- | --- | --- | --- | --- |
| `lattice.chroma` | 30 | 79.17 s | 7.48 s | **10.58×** |
| `primitives.chroma` | 400 | 6.68 s | 1.93 s | **3.46×** |
| `chamber.chroma` | 200 | 3.37 s | 1.57 s | **2.15×** |
| `translucency.chroma` | 60 | 27.28 s | 12.85 s | **2.12×** |
| `fog.chroma` | 30 | 29.61 s | 15.17 s | **1.95×** |
| `shapes.chroma` | 200 | 19.01 s | 10.36 s | **1.83×** |
| `glass.chroma` | 200 | 27.97 s | 16.08 s | **1.74×** |
| `cornell.chroma` | 200 | 15.30 s | 8.86 s | **1.73×** |
| `csg.chroma` | 200 | 6.06 s | 3.50 s | **1.73×** |
| `magnify.chroma` | 200 | 39.86 s | 23.34 s | **1.71×** |
| `sweeps.chroma` | 200 | 14.60 s | 9.24 s | **1.58×** |

The iteration's stated target was half the time on `cornell.chroma` and `glass.chroma`. Both
land at 1.73×, a little short of it; every other scene is at or past it.

---

## How this was measured, and how measuring it went wrong twice

`--samples <n>` renders to a fixed sample count and reports the time over every sample but the
first, which carries the driver's compilation of the fragment shader. `--error <percent>`
stops at a stated noise level instead, which is the metric that lets a variance reduction be
compared with a tracing speed-up: a sampler that halves the noise per sample is worth more than
a 10% sample-rate gain, and samples per second scores it as a loss.

Two methodological failures are worth recording, because both produced confident wrong answers.

**Drift.** The machine got slower over the session: `cornell.chroma`'s baseline moved from
14.91 s to 16.06 s over about two hours, with no change to the code. Any before/after
comparison separated by more than a few minutes is measuring the room temperature. Everything
above is an **interleaved A/B** — the baseline build lives in a `git worktree`, and each pair of
runs is back to back.

**Process wall clock.** Timing the whole process instead of the render inside it produced not
merely noise but an inverted sign: it reported the packed span, which is a 1.7× *gain*, as a
1.2× loss, and reported a build identical to one measured minutes earlier as 40% slower. The
in-application clock with `glFinish` is the only number quoted here.

---

## What was kept

### 1. The span stack, one word narrower — 1.7× to 2.0× on every scene

`Span` carried `tIn`, `tOut` and two surface codes: four words. The two codes are `±(primitive
index + 1)` and fit in sixteen bits each, so they now share one int and a `Span` is three words.

That is the largest single speed-up in this renderer's history, and the reason is arithmetic
about storage rather than about instructions. A span list holds `MAX_SPANS` of them, the tape
walk holds `MAX_STACK` lists live at once, and each merge needs one more list — 132 words, far
past what a fragment shader keeps in registers. The whole span stack therefore lives in local
memory, and every tape instruction reaches into it. A quarter off that structure is a quarter
off the traffic, and a quarter was what this shader was over by.

| Scene | Before | After | Gain |
| --- | --- | --- | --- |
| `fog.chroma` | 29.58 s | 14.88 s | 1.99× |
| `glass.chroma` | 26.90 s | 15.03 s | 1.79× |
| `cornell.chroma` | 15.22 s | 8.78 s | 1.73× |
| `lattice.chroma` | 79.15 s | 62.84 s | 1.26× |

Roadmap item 4 listed this as "the other half" of a change whose main point was raising
`MAX_SPANS`. It turned out to be the whole point. `MAX_SPANS` is deliberately left at 8: raising
it would spend the headroom this bought.

### 2. Bounding-box guards — 8.4× on `lattice.chroma`, nothing anywhere else

A new tape instruction, `OP_BOUND`, sits ahead of a subtree and carries a world-space box and a
jump target. A ray that misses the box pushes an empty span list and jumps past the whole
subtree. The empty list is what keeps the stack machine balanced without any operator learning
that the instruction exists — `a ∪ ∅ = a`, `a ∩ ∅ = ∅`, `a \ ∅ = a`, and a root that resolves an
empty list finds no surface.

Measured on top of the packed span:

| Scene | Without guards | With guards | Gain |
| --- | --- | --- | --- |
| `lattice.chroma` | 62.84 s | 7.48 s | **8.40×** |
| every other scene | — | — | not emitted |

`lattice.chroma` is 125 cells of a sphere and up to three struts. A ray meets one cell and now
skips the other 124 without fetching a matrix or solving a quadric. Nothing else in the
repository is built that way, which is the point of the next paragraph.

**Guards are a whole-scene decision.** Their benefit is local, but their cost is not: one guard
anywhere compiles the branch into the whole shader, and that alone cost `fog.chroma` 11% — a
scene whose three tiny operators could never repay it. So `CsgTapeBuilder.GuardsPayFrom` asks
whether the *scene* is long enough to have anything worth skipping, and below 100 instructions
the tape carries no guards at all. The largest hand-written scene in the repository is 40
instructions; the smallest generated one is 850. Nothing lands near the crossover.

### 3. Compiling the shader for the scene — the enabler

`uHasTransmission` and `uHasMedia` were uniforms, and are now `#define` symbols set from the
compiled scene, joined by `CHROMA_BOUNDS`. A uniform is constant for the whole draw, so
branching on one costs nothing to *execute*; what it costs is to **exist**. The untaken side is
still compiled and still holds registers in the schedule around it.

The measurement that forced this: adding the `OP_BOUND` branch to `runTape` cost `fog.chroma`
**2.3×** — on a branch that scene never executed, before a single guard was emitted into its
tape. An empty branch in the same place cost nothing, so the price was the body, not the
dispatch. Behind a `#if`, it costs that scene nothing at all.

The direct speed effect on scenes that were already lean is small — within noise on
`cornell.chroma`, about 1.7% on `glass.chroma`. Its value is that it makes the guards shippable.

### 4. Vertical sync, off — 3.46× on `primitives.chroma`, 2.15× on `chamber.chroma`

`WindowOptions.Default` asks for vsync, which is right for an application that redraws the same
picture and wrong for one where a frame *is* a sample: it makes the refresh rate the sample
rate.

This was tested at the start of the iteration and made no measurable difference to any scene,
because every scene was slower than 60 samples per second. **That finding expired during the
iteration.** Once the span packing landed, `chamber.chroma` and `primitives.chroma` both sat
within a percent of exactly 60 samples per second — the two scenes that showed a 1.00× gain, and
not by coincidence. Their real gains only appeared once the cap was lifted.

### 5. One tape walk instead of two — no measurable change

The `trace` and `occluded` wrappers were separate call sites, and a call site is a copy: the
compiler inlines the tape walk into each. They are gone, and `anyHit` travels as a value.
Measured at parity on every scene. It is kept because it removes a copy of the largest function
in the shader and so buys room for everything after it, not because it is faster today.

---

## What was implemented, measured, and removed

### Russian roulette — a net loss

Roadmap item 1, and the one the roadmap was most confident about. Terminating paths
stochastically with a compensating weight, from bounce 3.

| Variant | `cornell` noise at 200 spp | `glass` noise at 200 spp |
| --- | --- | --- |
| none | 13.834% | 24.341% |
| roulette on every path | 14.569% | 31.192% |
| roulette on weak paths only | 14.263% | 28.223% |

The speed gain was 3–4% on most scenes and 34% on `fog.chroma`. The noise cost was larger
everywhere. On the metric that combines them — time to reach 6% error on `cornell.chroma` —
it measured 116.05 s with roulette against 110.55 s without.

The reason is architectural rather than a tuning failure. A warp costs whatever its
longest-lived thread costs, so killing three quarters of the paths in one frees no lane at all,
while the survivors' inflated weights are variance paid for regardless. Roulette pays on a CPU
path tracer where a terminated path really does stop costing.

This also leaves the fixed path length's bias in place — 6.3% on the disc of iteration 5 at
eight bounces. Removing it needs a different mechanism.

### A low-discrepancy sampler — ~0.1%

Roadmap item 2. Roberts' R₄ sequence computed on the host in double precision, with a fixed
per-pixel toroidal rotation, spent on the pixel jitter and the first light sample.

| Scene | Noise before | Noise after |
| --- | --- | --- |
| `cornell.chroma` | 13.834% | 13.822% |
| `glass.chroma` | 24.341% | 24.341% |
| `fog.chroma` | 59.389% | 59.389% |
| `lattice.chroma` | 26.756% | 26.807% |

Two stratified dimensions out of a path that spends dozens is not where the variance is. A real
gain needs many dimensions scrambled independently — an Owen-scrambled Sobol sequence — and even
that has little to work with on a five-bounce path. Removed rather than kept on faith: it cost a
uniform, a host computation and a parameter through four signatures, and bought nothing
measurable.

### Two structural variants that made things worse

**One write site into the span stack.** Folding the leaf, operator and guard branches into a
single `stack[sp] = produced` looked like the fix for the `fog.chroma` cliff. It cost
`cornell.chroma` 20% and `glass.chroma` 23%: the shared local it needs is another 33-word
structure, and copying through it is worse than writing twice.

**Guarding every operation regardless of scene size.** 29% faster on `lattice.chroma`, 11%
slower on `fog.chroma`. This is what established that the decision belongs to the scene.

---

## Not done

**Adaptive sampling** (roadmap item 5) is not implemented. It is the one item on the list that
can change the image if it is got wrong: spending samples where the error is only stays unbiased
if the per-pixel sample count is carried into the average, and the accumulation buffer has no
room for it — RGB holds the running mean and alpha the running mean of the squared luminance,
which the convergence meter needs. Adding it means a second render target and a change to the
buffer's layout, and it deserves its own measured pass rather than being appended to this one.

**Denoising and irradiance caching** remain deliberately out of scope: they produce an image the
samples do not support, which the iteration's rule excludes.

---

## The general lesson

Four of the six things that worked or failed here did so for reasons invisible in the source
text. Dead code cost a scene a factor of two. A struct one word narrower was worth more than
every algorithmic change combined. A branch never executed was the most expensive line in the
file. The measurement method inverted the sign of a result.

This shader is not instruction-bound; it is bound by how much state a thread carries. That is
the frame for reading every number above, and the first thing to check about any optimisation
proposed for it.

---

## Baseline before per-scene code generation

Captured on the tape interpreter at commit `9fc89ee`, immediately before the rewrite described
in [code-generation.md](code-generation.md) began. GeForce RTX 4070 SUPER, `--samples 32`, so
these are sample rates rather than converged renders — the exhaustive pass belongs at the end
of the iteration. The images are kept alongside, and they are what each emitter step is
compared against.

| Scene | Primitives | Instructions | Specialisation | Samples/s |
| --- | ---: | ---: | --- | ---: |
| primitives | 3 | 6 | lean | 135.1 |
| csg | 7 | 14 | lean | 49.9 |
| cornell | 8 | 16 | lean | 21.9 |
| sweeps | 5 | 10 | lean | 21.1 |
| shapes | 7 | 14 | lean | 18.8 |
| glass | 15 | 30 | transmission | 13.2 |
| chess | 77 | 226 | bounds | 9.8 |
| lattice | 425 | 975 | bounds | 3.9 |
| fog | 11 | 22 | transmission, media | 2.2 |

The two rows that frame the rewrite are `cornell` and `csg`: both are small, both are "lean",
and both still pay for `stack[4][8]`, a 32-slot crossing array, a 24-slot sweep array and a
quartic solver they never touch. Whatever per-scene generation is worth, it has to show up
there first.

## After per-scene code generation

Same hardware, same command, same session. `--samples 32`, so these are sample rates; the
converged comparison belongs at the end of the iteration.

| Scene | Samples/s before | after | | Generated lines | Widest root |
| --- | ---: | ---: | ---: | ---: | ---: |
| cornell | 21.9 | 271.0 | **12.4x** | 422 | 1 |
| fog | 2.2 | 37.7 | **17.1x** | 628 | 2 |
| lattice | 3.9 | 58.0 | **14.9x** | 11885 | 4 |
| glass | 13.2 | 114.1 | 8.6x | 811 | 2 |
| shapes | 18.8 | 112.2 | 6.0x | 1027 | 7 |
| csg | 49.9 | 249.3 | 5.0x | 663 | 4 |
| sweeps | 21.1 | 64.8 | 3.1x | 850 | 24 |
| chess | 9.8 | 24.4 | 2.5x | 3278 | 3 |
| primitives | 135.1 | 277.2 | 2.1x | 217 | 1 |

Every image is the same image. cornell was compared pixel by pixel against its baseline: three
samples out of ~102,000 differ, each by one step in one channel, which is what constant-folding
a matrix into the code instead of fetching it from a buffer does to the last bit of a float.
`sweeps` and `primitives` came out byte-identical. Every scene's convergence error matches its
baseline to the printed precision except `lattice`, at 26.732% against 26.756%.

### Where it comes from

The three largest gains are the three scenes that were paying the most for state they did not
use. `cornell` is eight convex primitives and was carrying `stack[4]` of eight-span lists, a
32-slot crossing array, a 24-slot sweep array, a 16-slot blob array and a quartic solver. It now
holds **one** span. `fog` was the scene iteration 11 measured a never-executed branch costing
2.3x; there are no unexecuted branches left in it. `lattice` is 425 leaves and 11,885 generated
lines — the case codegen was expected to fail on, and the one it helps most, because what it was
losing was never instruction count but occupancy.

`chess` gains least, and for a legible reason: 3,278 lines from 69 roots that are mostly the same
four shapes repeated. Deduplicating structurally identical roots into one function with an
instance table is the next step, and `chess` and `lattice` are what it is for.

`sweeps` is the scene whose widest root is 24 — a Bézier lathe of 24 segments, which is now sized
at 24 spans and 48 crossings. Under the interpreter it was clamped to 8 spans and 32 crossings
and truncated in silence.

## The two GPU paths

Same hardware and session, `--samples 64`. The fragment path is the default; the compute path
is `--compute`. Both render the same image — `csg` differs in 24 of 230,400 sampled pixels,
worst channel delta 7/255, which is the sub-pixel jitter's last bit at high-contrast edges.

| Scene | Compute | Fragment | |
| --- | ---: | ---: | ---: |
| primitives | 600.7 | 496.4 | 1.21x |
| csg | 477.0 | 436.3 | 1.09x |
| chess | 179.2 | 164.5 | 1.09x |
| chamber | 559.6 | 517.6 | 1.08x |
| magnify | 225.0 | 210.7 | 1.07x |
| fog | 94.8 | 92.8 | 1.02x |
| cornell | 341.1 | 347.5 | 0.98x |
| shapes | 297.7 | 312.1 | 0.95x |
| lattice | 85.6 | 90.5 | 0.95x |
| translucency | 218.4 | 233.1 | 0.94x |
| colonnade | 321.8 | 364.2 | 0.88x |
| glass | 274.6 | 347.6 | 0.79x |
| **sweeps** | **37.2** | **128.7** | **0.29x** |

A wash, with one bad outlier. `sweeps` has the widest root in the set at 24 spans — the heaviest
register load — which points at the compute profile allocating registers worse under pressure.
Storage buffers are not the cause: with `--tbo`, reading the scene tables through a sampler on
the compute path instead, `sweeps` measures 35.6 against 35.7.

The compute path is therefore opt-in. It was built to find out whether a newer OpenGL lifts the
driver's instruction ceiling, and it does not — the same scene is refused at instruction 65,886
as a fragment shader and 65,887 as a compute shader. See [gpu-backends.md](gpu-backends.md).

### The ceiling itself

`scenes/chess-full.chroma`, bisected. It was kept in the repository as the artifact that said
where the wall was.

What the driver counts is instructions after inlining every call and unrolling every
constant-bound loop, so generated lines are only a rough guide.

| Configuration | Primitives | Generated lines | Result |
| --- | ---: | ---: | --- |
| 32 pieces + board, before body sharing | 162 | 10,514 | refused |
| 32 pieces + board, with body sharing | 162 | 7,434 | refused |
| 32 pieces, board removed entirely | 98 | 5,002 | refused |
| 20 pieces + board | 130 | 6,588 | refused |
| 16 pieces + board | 118 | 5,967 | compiles |
| `chess-half`, 16 men | 126 | 6,436 | compiles, 4.9 samples/s |
| 32 pieces + board, **instanced** | 32 | 3,342 | **compiles** |

Roughly sixteen turned pieces. Deleting all sixty-four board squares did not rescue the full set,
so the cost was in the lathes and not in the count of solids. That is the observation instancing
turned into the last row.

### Instancing, and why it is really the tree

A repeated shape is emitted once and its placements go in a buffer with a BVH over them, so the
program holds one body per **distinct** shape. That is what lifted the ceiling. What made scenes
*faster* is the tree that comes with it: `traceScene` used to test every root's box in source
order, so a lattice of 125 cells paid 125 box tests per ray per bounce and now pays about seven.

256 samples, 1280×720, RTX 4070 SUPER, against the same build measured everywhere else here.

| Scene | Before | After | | Instances |
| --- | ---: | ---: | ---: | ---: |
| chess | 191.1 | 1106.7 | **5.79×** | 68 |
| lattice | 98.5 | 338.1 | **3.43×** | 124 |
| chess-half | 4.5 | 13.7 | **3.04×** | 80 |
| cornell | 643.7 | 673.7 | 1.05× | 0 |
| glass | 519.1 | 521.0 | 1.00× | 0 |
| colonnade | 664.7 | 662.9 | 1.00× | 0 |
| sweeps | 153.1 | 152.3 | 0.99× | 0 |

The bottom four are neutral because they emit **byte-identical** code: a scene shares nothing
until it holds 32 repeated placements, and eleven of the fourteen scenes in `scenes/` are under
that and render bit-identically to the build before this work. The threshold is not caution, it is
a measurement: sharing everything shareable cost `glass` 35% and `cornell` 18%, because a BVH walk
is a loop of dependent memory reads where a run of folded guards is independent work. See
[gpu-backends.md](gpu-backends.md).
