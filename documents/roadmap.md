# Roadmap

Where the project is going, in the order it is being built. Each iteration ends with
something runnable and demonstrable; nothing is built ahead of the iteration that needs it.

Correctness and a clean, replaceable structure come first. Performance was deferred by policy
through the first seven iterations — optimising an algorithm still being made correct obscures
it — and is now scheduled, under a rule that stops it trading the image away for speed.

This document is the record of what was built. What is proposed and not built is in
[suggestion.md](suggestion.md); what the next delivery contains is in
[current_version.md](current_version.md).

## Status

| Iteration | Deliverable | State |
| --- | --- | --- |
| 0 | Documentation and design | done |
| 1 | Scene parsing + hierarchy dump tool | done |
| 2 | First render: camera, lights, primitives | done |
| 3 | CSG operators | done |
| 4 | Correct lighting: bounces, PBR materials, soft shadows | done |
| 5 | Transparency, refraction, Fresnel, caustics | done |
| 6 | Six more primitives: cone, plane, torus, prism, lathe, blob | done |
| 7 | `sphereSweep`, Bézier lathes, string literals | done |
| 8 | Language revision: conditions and loops | done |
| 9 | Measured against the state of the art | standby |
| 10 | Participating media: scattering and fog | done |
| 11 | Speed, at equal image | done, less adaptive sampling |
| 12 | Per-scene code generation | done |
| 13 | The illustrated manual | done |
| 14 | Instancing, and the ceiling moves | done |
| 15 | A cost model, and the ceiling goes | done |
| 16 | The silence before the first image | done |
| 17 | Cutting inside a top-level `union` | done |
| 18 | The loader stops counting | done |
| 19 | Randomness, and the rest of C's operators | done |
| 20 | Arrays, structs, and the maths that needed them | done |
| 21 | Documentation rules, and the manual in the archive | done, unreleased |
| 22 | The geometry the existing primitives were missing | done, unreleased |
| 23 | Rounding error, as a subject rather than a constant | done, unreleased |
| 24 | Meshes | done, unreleased |
| 25 | A height map | done, unreleased |

Iterations 21 to 25 are the 0.22.0 delivery and what remains before it is cut is listed in
[current_version.md](current_version.md).

The whole path from a scene file to pixels exists. Nothing of the original boilerplate
remains: the cube, its shaders and the matrix pipeline are gone, replaced by a fullscreen
quad and a ray tracing shader generated for the scene it draws.

**Why the last three sat in that order.** Iteration 9 is on standby, for the reasons under its
own heading. Media therefore came next, and came before speed so that the optimisation work
targeted the finished renderer rather than a snapshot of it. The manual is last because it
documents 8's syntax and 10's nodes — and, as it turned out, 12's limits.

**Iteration 12 was not on this list.** Per-scene code generation came out of iteration 11's
measurements rather than out of a plan, and it is numbered 12 in every document that refers to
it; the roadmap is the one that had no entry for it, and now does, below. The manual moved to 13
with it.

*(Iteration 8 came first for a reason that turned out to be worth less than it looked: every
scene written before the revision was supposed to be a scene written twice. The revision was
additive, so none of them was rewritten at all — see below. The ordering was still right, and
for the other reason: the manual would otherwise have described a language this roadmap had
promised to change.)*

---

## Iteration 0 — documentation and design

Fix the target before writing code. The two reference documents exist so that the
implementation iterations need no further research:

- [scene-language.md](scene-language.md) — grammar, node reference, POV-Ray appendix
- [csg-raytracing.md](csg-raytracing.md) — the interval algorithm, GPU encoding, GLSL limits
- [architecture.md](architecture.md) — the layering and why the boundaries sit where they do

Decisions locked in during this iteration:

| Question | Decision | Rationale |
| --- | --- | --- |
| Ray/CSG method | Exact analytic intervals (Roth), not SDF raymarching | Exact silhouettes and strict CSG semantics; an approximate `max(a, -b)` is not the same solid |
| Scene → GPU | Data buffer + generic GLSL interpreter, not code generation | One stable, debuggable shader; changing scene costs an upload, not a recompile — **reversed in iteration 12**, see [code-generation.md](code-generation.md): the interpreter's arrays were sized for the worst scene anyone might write, and paid for by every scene that was not it |
| OpenGL version | Stay on 3.3 Core | No SSBO, so texture buffers instead — worth it to keep the existing target and the widest driver support |
| Scene language | New JS-flavoured dialect | Readability and a syntax layer we control; will be revised for loops and macros |

---

## Iteration 1 — parsing and hierarchy

**Deliverable.** `Chroma.SceneDump scenes/csg.chroma` parses a scene file and prints the
solid hierarchy. Nothing is rendered.

```
$ dotnet run --project src/Chroma.SceneDump -- scenes/csg.chroma
Camera   position <0, 2, -6>  lookAt <0, 0, 0>  up <0, 1, 0>  fov 45

Lights
  +- PointLight        position <2, 4, -3>  color <1, 1, 1>  intensity 1
  `- DirectionalLight  direction <-0.57735, -0.57735, 0.57735>  color <0.25, 0.25, 0.35>  intensity 1

Solids
  +- Difference  material=red  translate <-1.8, 0, 0>
  |  +- Box  min <-1, -1, -1>  max <1, 1, 1>
  |  `- Sphere  center <0, 0, 0>  radius 1.3
  `- Difference  material=steel  translate <1.8, 0, 0>  rotate <0, 20, 0>
     +- Intersection
     |  +- Box  min <-1, -1, -1>  max <1, 1, 1>
     |  `- Sphere  center <0, 0, 0>  radius 1.35
     `- Union
        +- Cylinder  base <0, -2, 0>  cap <0, 2, 0>  radius 0.5
        +- Cylinder  base <-2, 0, 0>  cap <2, 0, 0>  radius 0.5
        `- Cylinder  base <0, 0, -2>  cap <0, 0, 2>  radius 0.5
```

**What was built.**

1. A four-project solution — `Chroma.Core`, `Chroma` (the existing app, moved to
   `src/`), `Chroma.SceneDump`, and `tests/Chroma.Core.Tests`.
2. `Sdl/Source/` — `SourceText`, `SourceSpan`, `Diagnostic`, `DiagnosticBag`.
3. `Sdl/Lexing/Lexer.cs` — the token set from
   [scene-language.md](scene-language.md#writing-a-file).
4. `Sdl/Syntax/Parser.cs` — the EBNF, into an AST that knows **no** node names.
5. `Model/` — `Solid`, `Sphere`, `Box`, `Cylinder`, `Union`, `Intersection`, `Difference`,
   `Transform`, both `ISolidVisitor` interfaces, plus `Camera`, `PointLight`,
   `DirectionalLight`, `Material`, `Scene`.
6. `Sdl/Binding/` — `Evaluator` with `let` scope, `BlockReader`, `INodeBinder` +
   `NodeBinderRegistry` with ten binders, `SceneBuilder`; `SceneLoader` as the façade.
7. `scenes/primitives.chroma`, `scenes/csg.chroma` and `scenes/diagnostics-demo.chroma`.
8. `SceneDump` — a `HierarchyPrinter : ISolidVisitor`; non-zero exit on any error.
9. 77 tests across the lexer, parser, evaluator, binding and diagnostics.

**Verified.** Both valid scenes dump correctly and exit zero; `diagnostics-demo.chroma`
reports its four planted errors in a single run, with exact line and column, and exits
non-zero.

---

## Iteration 2 — first render

**Deliverable.** `dotnet run --project src/Chroma -- scenes/primitives.chroma` opens the
window on a sphere, a box and a cylinder, lit by one point light and one directional light.

Primitives already return **spans**, which is the shape everything else plugs into. What is
not built yet is the merging: with leaves only on the tape, the loop keeps the nearest entry
among them, which is the implicit union of the top-level solids. Writing fixed-size span
lists now would have been speculative — nothing would exercise them.

A scene containing an operator is **refused with a diagnostic naming it**, rather than drawn
as the union of its operands. A picture that is wrong in a way the file does not explain
costs far more to diagnose than a message saying the feature is not there.

**What was built.**

1. `Compilation/` — `GpuLayout` (the buffer strides, shared with the shader), `SpanBudget`,
   `CsgTapeBuilder` as an `ISolidVisitor<SpanBudget>`, and `SceneCompiler` as the façade.
   Every primitive is reduced to a canonical form with its dimensions baked into one inverse
   matrix, so the shader reads no shape parameters at all.
2. `Model/Camera.CreateRayBasis` — the camera trigonometry, in the model so it can be tested
   without a GL context. `Solid.Origin` carries a source span so compilation errors point at
   a line.
3. `SceneLoader.TryLoadCompiled` — loading and compiling share one diagnostic bag, so a
   compilation error reads like a syntax error.
4. `Rendering/FullscreenQuad.cs` replaced `Cube.cs`; `Rendering/SceneBuffers.cs` uploads the
   three texture buffers; `Shader` gained the `int`, `float`, `Vector3` and array overloads.
5. `Shaders/raytrace.vert` and `raytrace.frag` replaced the cube shaders. No depth buffer and
   no depth test: a fullscreen quad has no depth complexity and visibility is resolved along
   the ray.
6. `Program.cs` — the scene file is a required argument, loaded and compiled **before** any
   window exists, so a scene error lands on the console rather than behind a black window.
7. 20 more tests: the camera basis, and the matrix round-trip for all three primitives.

**Verified.** `scenes/primitives.chroma` draws a sphere, a box and a cylinder with exact
silhouettes, Blinn-Phong highlights and a cool directional fill. The box is visibly rotated,
which is what proves the baked inverse and the inverse-transpose normal are right. The scene
stays centred and undistorted from 2.9:1 down to 0.8:1.

**Found on the way.** The sample scenes placed the camera at *negative* Z, a POV-Ray habit
that contradicts this project's documented right-handed convention and mirrored every scene
left to right. The scenes and the documentation examples were corrected, and
[scene-composition.md](scene-composition.md#coordinate-system) now spells out the consequence.

**Not covered by tests.** Transform composition and material inheritance *through a parent*
cannot be reached yet: only operators have children, and operators are refused. Iteration 3
makes both testable.

---

## Iteration 3 — CSG operators

**Deliverable.** `scenes/csg.chroma` renders a box with a spherical cavity, correctly lit
inside the cavity, and nested operators behave.

**Work.**

1. `csgUnion`, `csgIntersection`, `csgComplement` in GLSL over fixed-size span lists;
   `difference` as `A ∩ complement(B)`.
2. The stack machine loop over the tape in the fragment shader.
3. Normal flipping for subtracted surfaces, via the sign bit of the surface reference.
4. Shadow rays, reusing the tape evaluation with an any-hit query.
5. CPU-side binarisation of n-ary operators and span-budget validation, with an explicit
   diagnostic on overflow rather than silent truncation.

Everything needed was already specified in
[csg-raytracing.md](csg-raytracing.md#the-three-operators); no research was required for
this one.

**What was built.**

1. `csgUnion`, `csgIntersection` and `csgComplement` in GLSL over fixed-size span lists, with
   `difference` as `A ∩ complement(B)` — one small complement plus the intersection that
   already existed, rather than a third merge loop with its own way of being subtly wrong.
2. `runTape`, the stack machine: one loop, an explicit stack of `SpanList`, no recursion.
3. The signed surface reference, so a subtracted surface returns its normal negated. This is
   what makes the inside of a cavity shade instead of going black.
4. Shadow rays, as the same machine in `anyHit` mode: it stops at the first occluder and
   skips the "started inside" rule, since a surface must not shadow itself.
5. n-ary operators binarised into a left-leaning chain on the CPU, and the span/stack budget
   computed per subtree and **rejected with a diagnostic** on overflow, naming the innermost
   offending operator.

**Decided along the way.** A fifth opcode, `END_ROOT`, closes each top-level solid so the
shader resolves roots one at a time instead of merging them. That makes the span budget a
*per-root* limit: a scene may hold any number of separate solids however tight `MAX_SPANS`
is. Merging instead would have let nine plain spheres overflow a budget that comfortably
renders a nine-way CSG tree. The one case it gets wrong is documented in
[csg-raytracing.md](csg-raytracing.md#gpu-representation-a-post-order-tape).

**Verified.** `scenes/csg.chroma` draws both solids: a box whose spherical cavity is lit
from inside, and a rounded cube — `intersection` of a box and a sphere — bored through on
all three axes by a `union` of three cylinders. A scratch scene with a ground slab confirmed
the rest: `union` gives one silhouette and one merged shadow, `intersection` the expected
lens, `difference` a clean bore, and every solid casts a shadow with no acne.
`scenes/primitives.chroma` is unchanged, so nothing regressed.

**Closed a coverage gap.** Operators now have renderable children, so transform composition
and material inheritance *through a parent* — untestable in iteration 2 — are covered.

---

## Iteration 4 — correct lighting

**Deliverable.** A closed box lit by one emissive panel, in which the white ceiling is
visibly tinted by the coloured floor. Colour bleeding is the cheapest possible proof that
light actually bounces — a direct-lighting renderer cannot fake it. Alongside it, a mirror
sphere reflecting its neighbours.

Up to here every pixel is one ray and one shading call: light travels from a light to a
surface to the eye, and stops. This iteration makes light *propagate* — a surface lit by
another surface, a surface seen in another surface. It is the largest single change to the
shader in the project, because it turns `trace once, shade once` into a loop.

**Research first.** This is the first iteration whose reference document did not exist yet.
[lighting.md](lighting.md) was written before any code, to the standard iteration 0 set:
complete enough that implementing against it needs nothing from the web.

**Decisions locked in.** Five questions had to be settled, and each one changed what gets
built.

| Question | Decision | Consequence accepted |
| --- | --- | --- |
| Whitted loop, or Monte Carlo path tracing? | **Accumulating path tracer** | A Whitted pass produces no indirect diffuse, so no colour bleeding, so the deliverable above is unreachable. The price is noise and an accumulation buffer |
| Keep Blinn-Phong, or an energy-conserving BRDF? | **Lambert + Cook-Torrance GGX** | Blinn-Phong invents or loses energy when summed over bounces. `specular`/`shininess`/`reflectivity` are **removed** in favour of `roughness`/`metallic`. A breaking change to every `.chroma` file |
| Delta lights only, or emissive solids? | **Both** — `radius` on `pointLight`, and `emission` on materials | Soft shadows for one field; a solid can be a light and be seen. A small emissive solid converges slowly — see below |
| Next-event estimation, or brute force? | **NEE**, reusing iteration 3's shadow rays | Converges incomparably faster. Costs one sampling routine per light shape |
| Fixed bounce depth, or Russian roulette? | **Fixed**, set by `render.maxBounces` | Biased but reproducible and trivial. Russian roulette stays noted for later |

**The hard point, found while writing the reference.** A CSG solid **cannot be sampled
uniformly** — there is no parameterisation of `difference { box, sphere }`, and nothing in
the interval algorithm provides one. Emissive materials therefore cannot be targeted by NEE;
they are found only by random bounces. The honest consequence is that a large emissive
surface converges well and a small one is noisy: `pointLight { radius }` is how to *light* a
scene, `emission` is how to be *seen*.

That has one happy side effect worth naming, because its absence reads like an oversight:
lights are not geometry and emissive solids are not sampled, so **no path is ever counted
twice and multiple importance sampling is unnecessary**. It stops being true the moment
emissive solids become sampleable.

**Work.**

1. **Accumulation.** Two `RGBA32F` textures ping-ponged through framebuffer objects, with a
   running average rather than a growing sum. All core in GL 3.3 — no version bump.
2. **A tone-mapped resolve pass** — exposure, ACES filmic, gamma. Skipping it makes a correct
   result look like a lighting bug.
3. **Per-pixel randomness.** A PCG hash seeded from pixel and frame index, with the state
   threaded through the bounce loop by hand — GLSL has no global state.
4. **The bounce loop**, carrying a throughput colour. `trace()` is reused unchanged.
5. **The PBR material**, and the migration of every scene, the `material` table in
   [scene-appearance.md](scene-appearance.md#material), and the GPU material layout.
6. **`pointLight.radius`**, sampled through the sphere's visible cone, normalised so that
   radius changes softness without changing brightness.
7. **A `render { }` node** for `maxBounces` and `exposure` — settings that are properties of
   a scene, not of the build.

**Watch for.** This is the iteration where performance stops being ignorable — every pixel
now traces the whole tape several times per frame. The response is *not* to start optimising
the span machinery, which would obscure an algorithm still being made correct; it is that
progressive accumulation converges while the camera is still, so an interactive frame stays
cheap and the image improves on its own.

**Verified.** `scenes/cornell.chroma` shows the floor and ceiling taking the colour of the
wall beside them, with no light aimed at either and no wall visible in that direction, and a
`metallic: 1, roughness: 0.05` sphere reflecting both coloured walls and its neighbour. A
scratch scene of three spheres at increasing heights over one floor gave penumbrae that
widen with occluder distance.

Two measurements rather than impressions:

- **The radius really is a pure softness control.** On a patch of floor lit in both renders,
  `radius: 0.9` and `radius: 0` measured 165.1072 and 165.1112 — a difference of 0.002%,
  which is noise. The 1.6% difference across the whole image is entirely the softer shadows.
- **The running average converges and holds.** Mean brightness over 24 seconds of a still
  camera moved 154.184 → 154.195 → 154.206, or +0.014% — residual noise tightening, not the
  slow drift a mis-weighted average would show.

**Found on the way.** A metal solid in an otherwise empty scene renders nearly black. That is
correct — a metal has no diffuse lobe and can only reflect its surroundings, and the
environment is black by default — but it made `scenes/csg.chroma` worse than in iteration 3,
so its steel material went back to a glossy dielectric with a comment explaining why, and the
metal demonstration lives in `cornell.chroma` where there is a room to reflect.

Two smaller consequences worth knowing: the `AMBIENT` constant is **gone**, since it stood in
for exactly the light the bounce loop now computes, and `BACKGROUND` is black because it is
no longer a backdrop but a uniform environment light.

---

## Iteration 5 — transparency, refraction and caustics

**Deliverable.** A glass sphere above a plane, casting a bright caustic spot; and a slab of
coloured glass that darkens with its thickness.

**Research first.** As with iteration 4, the reference document comes before the code —
`documents/transparency.md`, or a section of `lighting.md` if the two end up inseparable.

**Work.**

1. **Material fields.** `ior` (index of refraction), `transmission`, and an absorption
   colour. Language, binder, material table and documentation, as one change.
2. **Refraction in the bounce loop.** Snell's law, total internal reflection when the
   square root goes negative, and `reflect` vs `refract` chosen by the Fresnel term —
   Schlick's approximation is the usual choice and is accurate enough for dielectrics.
   Choosing *one* of the two paths at random per sample keeps the loop non-branching; the
   accumulation from iteration 4 averages them back together. That dependency is the reason
   this iteration comes second.
3. **Beer–Lambert absorption**, which is where the interval algorithm pays off a second
   time: attenuation needs the exact path length *inside* the solid, and a span is
   `[tIn, tOut]` — the length is already there, for free, with no extra intersection test.
4. **A `merge` operator.** A union of two transparent solids shows the internal faces where
   they overlap, because union keeps them; POV-Ray has a separate `merge` keyword for
   exactly this reason. It is a fourth opcode over the same span lists: union, then drop the
   surfaces interior to the result. Cheap here, and impossible to retrofit onto a mesh
   renderer.
5. **Transmittance shadow rays.** A shadow ray currently answers yes/no. Through glass the
   answer is a colour: accumulate absorption along the occluders instead of stopping at the
   first one.
6. **Caustics — the open problem.** Everything above is standard; this is not. A backward
   path tracer finds specular–diffuse–specular paths essentially never, which is why a naive
   implementation shows a glass sphere with a plain shadow under it and no bright spot. The
   three honest options, to be weighed in the research pass:

   | Option | Cost |
   | --- | --- |
   | Brute force, more samples | Works only for large area lights; a small light never converges. Zero new machinery. |
   | Photon mapping | Correct and general. Needs a light-emission pass writing to a buffer and a spatial lookup structure — that is a compute shader, i.e. **the first genuine reason to leave OpenGL 3.3** for 4.3. |
   | Approximate or skip | Keep transparency and refraction, drop the caustic. Defensible if the omission is stated. |

   Note that caustics also *require* iteration 4's emissive area lights: a delta light has
   no surface for photons to leave from.

**Done when** the glass sphere refracts what is behind it, thick coloured glass is visibly
darker than thin, two overlapping transparent solids under `union` show no internal seam,
and — subject to the choice made in item 6 — a caustic appears under the sphere.

*(That criterion said `merge` when it was written. See correction 1 below.)*

**Decisions locked in.**

| Question | Decision | Consequence accepted |
| --- | --- | --- |
| Polished glass only, or frosted too? | **Microfacet BTDF** (Walter et al. 2007) | `roughness` means the same thing on both sides of a surface, so frosted glass and translucent plastic come for free. The refraction Jacobian is the delicate part |
| How to reach caustics? | **Brute force, lit by an emissive solid** | No new pass, no photon buffer, no version bump. The price is noise: this is the slowest scene here to converge |
| Add a `merge` operator? | **No — it already exists** | See below |
| Nested transmissive media? | **Not supported**, documented | Glass inside glass gives a wrong result. Named in [transparency.md](transparency.md#limits-of-this-implementation) rather than hidden |

**Three things this roadmap got wrong**, found while building it. They are recorded because
the reasoning above is what someone would otherwise trust.

1. **`merge` was already built.** `csgUnion` coalesces overlapping intervals, and the
   interior surfaces vanish with them — the shader's own comment said so since iteration 3.
   POV-Ray needs a separate `merge` because its `union` is a shortcut that skips the interval
   merge; there is no such shortcut here to undo. Nothing was built, and the claim was tested
   instead: two overlapping glass spheres under `union` show no seam.

   The test turned up something better, though. Top-level solids are resolved **one root at
   a time**, so their spans are *not* merged — and two overlapping glass solids written at
   the top level really do show the interior faces. The distinction is invisible for opaque
   solids, which is why five iterations went by without it surfacing.
   [scene-composition.md](scene-composition.md#solids-at-the-top-level) now
   says so.

2. **Caustics are not a reason to leave OpenGL 3.3.** Photon mapping was described above as
   needing compute shaders, hence 4.3. Transform feedback has been core since **3.0** and
   would carry a photon-splatting implementation. That option was not taken, but it was not
   blocked by the version either.

3. **The path length inside a solid is not quite free.** A span is `[tIn, tOut]`, but a
   refracted ray *bends* at the boundary and leaves somewhere other than the original
   `tOut` — it has to be traced again. What the interval algorithm really gives free is
   **which side of the surface the ray is on**, which `resolveRoot` already computes and
   which a mesh renderer has to infer from a normal.

**Research first, again.** [transparency.md](transparency.md) was written before any code,
to the same standard: Snell, Fresnel with the correction that matters from inside the dense
medium, the Walter BTDF with its Jacobian carried through to the point where everything
cancels, Beer–Lambert, transmissive shadow rays, why caustics are hard — and a **Limits**
section naming, with its symptom, every wrong image the implementation can produce.

**Verified.** `scenes/glass.chroma` shows all four claims at once: the coloured bars behind
the sphere appear left-right **inverted**; the thicker of two slabs of the same glass is
markedly darker; two overlapping spheres under `union` have no seam; and a bright patch sits
on the floor beneath the sphere, shaped like the ceiling panel, because a ball lens images
its light source.

Four measurements rather than impressions:

- **The caustic is real, not a bright shadow.** Rendering the same room with and without the
  sphere, the floor patch beneath it measured **1.95×** brighter *with* the glass than the
  bare floor receives, and 2.18× the floor beside it. A caustic is exactly this: more light
  delivered than would arrive unaided.
- **No regression on any earlier scene.** `cornell.chroma` at 60 s matched its iteration-4
  render to within **0.04%** on every region measured. `ior: 1.5` reproduces the old 0.04
  reflectance exactly, and Schlick is affine in `F0`, so the metallic blend is identical too.
- **An `ior: 1.0` solid is optically invisible** — no lensing, no displacement, no
  reflection — but not free. It still spends two bounces, and against a fixed `maxBounces`
  that shows as a faint darker disc: **6.3%** below the wall beside it at 8 bounces, **3.8%**
  at 16. That is the documented bias of a fixed path length, measured.
- **The fast shadow path is worth about 5%, not more.** An opaque scene keeps iteration 3's
  early-out shadow ray. Flipping only the flag on `cornell.chroma` — a transmission too small
  to change a pixel — cost **5.5%** of the sample rate, reproducibly. The transmissive walk
  is cheaper than expected because it usually stops at the first opaque occluder anyway.

**Found on the way.** The three-lobe sampler drew one microfacet from `D` and rejected the
sample when that facet faced away from the viewer. Correct for both specular lobes — and
wrong for the diffuse one, which never goes through that microfacet. Matte surfaces lost
most of their samples, and `cornell.chroma` came out **7% darker overall** and 12% darker on
the floor — every region losing energy in the same direction. That is what gave it away: a
real lighting bug is almost never a deficit of similar size on a red wall, a white floor and
a metal sphere at once.

---

## Iteration 6 — six more primitives

**Deliverable.** `scenes/shapes.chroma` draws a cone, a torus, a blob, a lathed vase and a
bored hexagonal prism, standing on an infinite plane.

Up to here the renderer had three shapes, all convex, all reducible to a canonical form by an
affine map, all needing exactly one span. Each of those three facts was load-bearing somewhere,
and this iteration breaks all three.

**Decisions locked in.**

| Question | Decision | Consequence accepted |
| --- | --- | --- |
| Where do shape parameters live? | **The two spare slots in the primitive record**, plus a fourth texture buffer for lists | The claim that "the shader reads no shape parameters" is retired. A cone's taper and a torus's minor radius are ratios and cannot be scaled away |
| Splines for `prism` and `lathe`? | **Linear only** | POV-Ray's quadratic, cubic and Bézier splines are a CPU-side tessellation into segments. Nothing in the shader would change, so nothing is lost by deferring them |
| Several contours per prism? | **No** | POV-Ray fills them even-odd to punch holes, because its prism is not a CSG shape. This one is: write a `difference` |
| Blob components | **Spheres only** | A cylindrical component's field is piecewise where the spherical one is not, and each piece needs its own solve |
| Spindle torus (`minor >= major`)? | **Refused with a diagnostic** | POV-Ray offers four ways to interpret its inside. None of them is a shape a CSG operand can be relied on to be |
| Raise `MAX_SPANS` for the non-convex shapes? | **No** | A prism of 16 points, a lathe of 8 or a blob of 8 fit the existing budget, and raising it costs register pressure on every scene. The existing overflow diagnostic already names what does not fit |

**What was built.**

1. Six model classes and their binders, `blobSphere` as a component node, and the shared
   reader for interleaved point lists.
2. `GpuLayout.SpansFor` — the per-kind span cost, replacing the constant that assumed every
   leaf was convex — and the overflow check moved so a single leaf can trip it.
3. A fourth texture buffer, `uShapes`, for prism and lathe edges and blob components. The
   accumulation history moved from texture unit 3 to 4 to make room.
4. GLSL span and normal functions for all six, a Ferrari quartic solver shared by the torus
   and the blob, and the even-odd contour test that settles the sign of a prism's or a lathe's
   normal without demanding a winding of the file.
5. `scenes/shapes.chroma`, and 37 tests over canonicalisation, the shape buffer's layout, the
   span budgets and every diagnostic — 132 to 169.

**Verified.** All six render, with correct silhouettes, correct shadows and correct normals.
The bored prism was checked from directly overhead, where a hexagon and a circular bore are
unambiguous; a one-component blob was checked against the sphere its threshold implies.

**Found on the way — three numerical faults, and none of them algebraic.** The formulas were
right the first time; the arithmetic was not, and each fault produced a picture that looked
like a bug in the geometry.

1. **The quartic's coefficients have to be built near the object.** They grow as the fourth
   power of the ray origin's distance while the roots stay near the shape, so a camera six
   units out spends three or four digits of a 32-bit float before the solver starts. Measured
   on a blob: root residuals of 1e-4 against a coefficient scale of 1000, falling to 1e-7 once
   the ray was re-origined at the interval being solved.

2. **Ferrari's factorisation needs checking, not just computing.** When `q` is zero the
   resolvent cubic's root is zero too — but it is computed as the difference of two nearly
   equal cube roots, so it lands on a small *positive* number as readily as on zero, and
   `sqrt` turns 1e-5 of noise into an `α` of 3e-3. That is large enough to pass any absolute
   test for "is `α` zero" and small enough to make `q/α` meaningless. The solver then returned
   four confident "roots" whose residual was 0.77. The fix is to test the identity Ferrari
   guarantees, `βγ == r`, and fall back to the biquadratic factorisation when it fails.
   Symptom: a blob wrapped in an onion of invented shells.

   That fix has since been replaced by one that removes the fault instead of catching it: the
   same identity gives `γ - β` without dividing by `α` at all. Detecting the bad split had left
   the torus with a dark seam wherever `q` passed through zero, because the biquadratic
   fallback answers by dropping `q`, and on a torus `q` is not small only when it is
   negligible. See [csg-raytracing.md](csg-raytracing.md#solving-the-quartic).

3. **A Newton polish is a refinement and has to be guarded as one.** Two steps against the
   original polynomial recover what the resolvent lost — except near a double root, where the
   derivative nearly vanishes and the step jumps somewhere unrelated. A blob's silhouette is
   made of near-double roots end to end, so an unguarded polish put a dark shell around every
   blob in the scene.

**And one that was not numerical.** Fixing the parity of crossings by collapsing coincident
ones is a trap: two faces meeting at a vertex legitimately produce two crossings a hair apart,
and merging those breaks the parity it was meant to protect — a lathe came out with bands you
could see straight through. Duplicates are prevented instead, by half-open ranges so each edge
owns its starting vertex and not its ending one. The prism already did this; the lathe did not.

**A false alarm worth recording**, because the next person will see it too: a hexagonal prism
appeared to have a phantom wedge attached to one side. It does not. One of its faces points
away from both lights, and `BACKGROUND` has been black since iteration 4, so the face has
nothing to light it but bounce from the floor. Rendering it from directly overhead settled the
question in one image. The lesson is iteration 4's, restated: in a scene with no environment
light, "black" is not evidence of broken geometry.

---

## Iteration 7 — sweeps and curves

**Deliverable.** `scenes/sweeps.chroma` draws a tapering `sphereSweep`, a second one closed
into a ring and cut in half by a `difference`, and a `lathe` whose outline is three cubic
Bézier curves and whose surface is smooth rather than faceted.

Iteration 6 added six primitives without the shader ever running out of room. This one found
where the room ends, and most of the work went into that rather than into the two features.

**The measurement that decided everything.** `MAX_SPANS` was to be raised from 8 to 16 so a
tessellated curve would fit the span budget. It cannot be. On a GeForce RTX 4070 SUPER:

| `MAX_SPANS` | Result |
| --- | --- |
| 8 | 13.41 samples/s |
| 9 | 12.32 samples/s (−8%) |
| 10, 12, 14 | link fails: `Internal error: assembly compile error … too many temporaries` |
| 16 | link fails: `cannot locate suitable resource to bind variable … Possibly large array` |

A wall, not a slope, and the renderer was already one step from it. The span stack is
`MAX_STACK` span lists held live across the whole tape walk, and the compiler inlines the tape
walk into both `trace` and `occluded`, so every array inside it is counted more than once.

**What that forced.** Crossing arrays turn out to be nearly free by comparison — one local
array inside one function — so `MAX_CROSSINGS` went 16 → 32 at no measurable cost, and the
span bound for point-list primitives was **clamped** to `MAX_SPANS` instead of counting
segments. That clamp is a deliberate departure from "never truncate silently" and is written
up as one in [csg-raytracing.md](csg-raytracing.md#fixed-size-arrays-and-the-span-budget). The
justification is that a curve flattened into segments is not a more complicated solid: a vase
occupies one or two spans whether it is drawn with 6 segments or 60. Size limits stayed
strict, so nothing silently overruns an array.

**Decisions locked in.**

| Question | Decision | Consequence accepted |
| --- | --- | --- |
| How to name a spline in a language with no strings? | **Add string literals** | A fourth value type, for naming variants only — no escapes, no concatenation, no multi-line. Reusable for every enumerated field after this one |
| Curved outlines: CPU or GPU? | **Flatten on the CPU** | The shader never learns a curve existed, so a Bézier lathe costs exactly what a polyline lathe of the same vertex count costs |
| Faceted shading on a flattened curve? | **Blend normals across joints**, opt-in | Flattening fixes the silhouette but not the shading, and a Bézier vase without this reads as a stack of rings. Carried in the sign of the segment count, since both parameter slots were taken |
| `sphereSweep` splines | **Linear only** | Each segment is then the convex hull of two spheres and solves in closed form — which is also why POV-Ray's `tolerance` has no equivalent here |

**What was built.**

1. String literals through the whole front end — `TokenKind.String`, `StringExpression`,
   `StringValue`, `BlockReader.Keyword` — plus the arithmetic paths learning to refuse them by
   name rather than by falling through the object case.
2. `SphereSweep`: binder, model, tape, and in GLSL a round-cone span, a depth-counter union
   over the segments, and the tangent-sphere normal.
3. Cubic Bézier flattening for `lathe`, and normal blending for the outlines that came from a
   curve.
4. `GpuLayout.MaxCrossings`, `MaxSweepSpheres` and `MaxBlobComponents` as explicit shader
   array sizes, enforced in the binders where a diagnostic can name the field.
5. `scenes/sweeps.chroma`, and 16 more tests — 169 to 185.

**Verified.** All three shapes render correctly: the sweep's joints are seamless and its caps
hemispherical, the ring sweep cuts cleanly under `difference`, and the Bézier vase has a
continuous specular highlight with no banding. The linear lathe in `scenes/shapes.chroma` is
pixel-identical, which is what proves normal blending is genuinely opt-in.

**Cost, measured.** Same conditions as above, before iteration 6 versus all ten primitives:

| Scene | Before | After |
| --- | --- | --- |
| `cornell.chroma` | 13.41 /s | 13.59 /s |
| `glass.chroma` | 7.93 /s | 8.04 /s |
| `shapes.chroma` | 11.68 /s | 11.33 /s |

A scene that does not use the new primitives pays **nothing measurable** — the differences on
`cornell` and `glass` are below the run-to-run spread. One that does pays about 3%, and that
is the larger crossing arrays rather than the tracing.

**Found on the way.** Two things, both about the ceiling rather than about geometry.

1. **Adding `sphereSweep` at all broke the link**, at `MAX_CROSSINGS` 48, with
   `too many temporaries` and 514 registers listed. Its two parallel arrays — a position and a
   depth delta per event — are what tipped it. Each array now has its own size, tuned to what
   it actually needs rather than to one shared constant, and the working configuration was
   found by bisection: crossings 32, sweep events 24, blob events 16.
2. **The first Bézier vase was smooth in silhouette and faceted in shading**, which is easy to
   mistake for a tessellation that is too coarse. It is not — no step count fixes it, because
   the facets are in the normals rather than in the geometry. That is a general lesson about
   flattening curves and is why the blending exists.

---

## Iteration 8 — conditions and loops

**Deliverable.** `scenes/lattice.chroma` draws a 5×5×5 lattice of spheres joined by cylinders,
with the eight corner cells carrying a different material, in under twenty lines. Written out
by hand the same scene is around four hundred, which is why it has never been written.

The dialect has been provisional since iteration 0, where the decision table says so in as many
words. Everything up to here has been *description*: the parser builds an AST that knows no
node names, and the binder walks it exactly once. Control flow makes it *computation* — the
tree the binder walks stops being the tree the parser produced, and its shape depends on values
that do not exist until bind time.

**No research pass.** This is the only remaining iteration that needs no physics. What it needs
is one design decision, below, and the discipline to keep the diagnostics as good as they are.

**Work.**

1. **Control flow as syntax, not as text.** The fork that shapes everything else:

   | Option | Cost |
   | --- | --- |
   | A `#`-prefixed preprocessor ahead of the lexer, as POV-Ray does | Much the cheaper, and it forfeits the property seven iterations have protected: every diagnostic names a line and a column *in the file the user wrote*. After expansion those positions belong to generated text. It also gives the language a second scoping rule that does not match `let`'s |
   | Control-flow nodes the evaluator executes | Reshapes `Sdl/Binding/`: `Scope` becomes a stack of frames, `SceneBuilder` emits a variable number of solids per node, and `BlockReader` has to tolerate a block whose contents are not statically known. The `SourceSpan` already threaded through every node keeps pointing at the source, so diagnostics stay exact |

2. **Bounded iteration only.** `for (i in 0..n)` and not `while`. A scene file that loops forever
   is the one failure the loader has no way to report: it produces no diagnostic, no window and
   no exit code, which is the opposite of everything iteration 1 built. If `while` earns its way
   in later it arrives with an iteration cap and a diagnostic naming the loop.

3. **The expression grammar has to grow first.** The evaluator does arithmetic and nothing else
   — there are no comparisons and no boolean operators, and `if` is unusable without them. That
   is the prerequisite, and it is also what makes `if` at statement level (emit this solid or
   not) and `if` as an expression two sizes of the same feature rather than two features.

4. **`include`**, because the first thing a loop makes worth writing is a fragment worth reusing.
   One question comes with it: whether an included file's `let` bindings are visible to the
   includer, which is textual and POV-Ray's answer, or sealed, which is what makes a fragment
   safe to drop into a scene you did not write.

5. **Macros deferred, deliberately.** The Beyond entry has always said "loops and macros". A
   macro is a parameterised block returning a solid, which on the evaluator route is a callable
   value plus argument binding — small, once the frames of item 1 exist. On the preprocessor
   route it is textual substitution with no scoping at all. Splitting them keeps this iteration
   bounded and gives the decision in item 1 a second reason to go the way it goes.

6. **Migration of every scene.** The revision is expected to be breaking. The measurable form of
   "it is a superset" is in the closing criterion below.

**What loops break, and it is not the parser.** The interesting consequence is downstream.
`CsgTapeBuilder` binarises an n-ary operator into a *left-leaning* chain, and a left-leaning
chain is exactly the shape that keeps the shader's stack at depth two however many operands
there are — so a `union` of a thousand generated solids costs no stack at all. The span budget
is what gives instead: `MAX_SPANS` is 8 per root, and a ray crossing a `union` of many disjoint
spheres occupies one span per sphere it passes through. Loops make that scene trivial to write
for the first time, and two consequences are worth stating before someone meets them:

- The overflow diagnostic from iteration 3 names the innermost offending operator. For generated
  geometry that is not a place in the file anyone can point at. It has to name the **loop**, and
  the count that broke it.
- The obvious workaround — leave the generated solids at the top level rather than under a
  `union` — is not semantically free. Top-level solids are unioned but **not merged**
  (correction 1 of iteration 5), which is invisible for opaque solids and visible the moment one
  of them is glass.

**Done when** `scenes/lattice.chroma` renders; every scene from iterations 1–7 renders
**pixel-identical** after migration, which is what proves the revision added rather than
changed; and a loop that overruns the span budget is refused with a diagnostic naming the loop
rather than the thousandth sphere.

**Decisions locked in.**

| Question | Decision | Consequence accepted |
| --- | --- | --- |
| Preprocessor, or evaluator? | **Control-flow statements the evaluator runs** | The route argued for above, taken. `Sdl/Binding/` was reshaped as predicted; positions still name the file someone wrote, including inside an included fragment |
| Where may `if` and `for` appear? | **Anywhere a field or a child may** — a block and a file became the same statement list | One hierarchy instead of two, so one parser and one evaluator for control flow. The price is one rule: a field outside a block, and a scene item inside one, are rejected where the list is consumed rather than by the grammar |
| How does `if` avoid being ambiguous with an object literal? | **By position, not lookahead** | After `if (…)`, a `{` opens a *body* where a statement is expected and an *object literal* where a value is expected. Each reading is the useful one in its place, and neither costs a token of lookahead |
| Is a range inclusive? | **Half-open**: `0..n` runs `n` times | Matches every `range(n)` a reader has met. `for (i in 1..12)` reads worse, and that is the only case it reads worse in |
| Truthiness? | **None** — `if (count)` is an error | A boolean is produced only by a comparison or a literal. Costs a fifth value type and buys a language where no condition means something the file did not say |
| Are an included file's bindings visible to the includer? | **Asymmetric: out, but not in** | The question as posed was binary. Out, because a fragment that exports nothing is not worth including; not in, because a fragment that can read its host means something different in every scene it is dropped into. Parameterising one is what macros are for |
| Guard against a loop that runs for an hour? | **A budget of 100 000 iterations per load** | `for` cannot loop forever, but `for (i in 0..1000000000)` parses, and a loader that disappears reports nothing at all. The budget makes that reportable, and no scene worth writing comes near it — the lattice spends 125. **Removed in iteration 18**: that last clause was the whole justification, and it expired. `cube-4.chroma` spends 328 419 and renders at 3% of the instruction budget |

**What was built.**

1. The lexer's one reserved word became eight — `if else for in true false include` beside
   `let` — plus `..` and the nine comparison and boolean operators, each tried as a pair
   before as a single character.
2. **`SourceSpan` carries its `SourceText`.** This is what `include` really costs: an
   included fragment's offsets index a different string, and reporting them against the
   includer would put every diagnostic on whatever line happens to share the offset. Null
   means "the file being loaded", so `default` and the parser's synthetic spans are unchanged
   and the common case stays free.
3. **One statement hierarchy** for the top level and for the inside of a block. `FieldEntry`
   and `ChildEntry` became `FieldStatement` and `ExpressionStatement` beside `LetStatement`,
   `IfStatement`, `ForStatement` and `IncludeStatement`, and `ObjectExpression` holds a list
   of them. The *bound* side — `BoundField`, `BoundChild`, `BlockReader` — is untouched, so
   not one of the twenty binders changed.
4. `Scope` became a chain of frames: one per block, per `if` body and per loop iteration. The
   no-shadowing rule now reads up the whole chain, loop variables included, which is what
   makes a `let` in a loop body fresh each time round instead of colliding with itself.
5. `Evaluator.Execute` — the fold became an interpreter. `if`, `for`, `include`, the
   comparison and boolean operators with short-circuiting, `if` as an expression evaluating
   only the branch taken, and the iteration budget.
6. **The span budget learned to name a loop.** `LoopOrigin` rides from the entries an
   iteration produces, through `ObjectValue` and `SolidBinder`, onto `Solid.Generator`, and
   `CsgTapeBuilder` reports at the `for` with its count. A hand-written overflow still names
   the operator, and both messages are tested.
7. `scenes/lattice.chroma`, and 67 more tests — 185 to 252.

**Verified.** `scenes/lattice.chroma` is 19 lines below the lights, and the hierarchy dump
shows exactly what it should: 125 cells, 125 nodes, 300 struts, 8 gold corners and no others.
It compiles to 850 tape instructions, 425 primitives and 2 materials, with a worst case of 4
spans and a stack depth inside the shader's limit; the renderer loads it, uploads it and runs
it without a GL or link error.

*(One thing here is asserted rather than measured, and is flagged rather than hidden: nobody
has looked at the image. The renderer has no non-interactive path — a scene goes in, a window
opens, and it stops when the window is closed — so "renders correctly" cannot be checked by a
script yet. Building that path is iteration 13's second item, and it is the point at which
this claim, and the pixel-identical comparison below, become measurements rather than
inferences.)*

The superset claim was measured, and in a stronger form than the criterion asked for. **No
scene was migrated at all** — no file in `scenes/` uses any of the seven new keywords as a
name, so the revision is additive in practice as well as in principle. Against a build of the
previous commit in a second worktree, the hierarchy dump of every scene from iterations 1–7 is
**byte-identical**, and `diagnostics-demo.chroma` still reports its four planted errors at the
same four line-and-column positions. All 185 tests from iteration 7 pass unchanged.

Byte-identical dumps are a stronger check than they look and a weaker one than "pixel-identical".
Stronger, because the dump is the whole bound scene — every solid, every transform, every
resolved material — so nothing that reaches the compiler can have changed without showing up
here. Weaker, because it stops at the front end, which is also the only place this iteration
touched: no shader, no buffer layout and no compilation path was modified except the value of
`MaxInstructions`, which only ever refuses scenes.

**Found on the way.**

1. **Loops strain the tape, not the span budget — and the tape's limit was the one nobody had
   looked at.** The section above expected `MAX_SPANS` to be what gives, and it barely moves:
   a ray crosses one lattice cell at a time, so the worst case is four spans against a budget
   of eight. What gives is `MaxInstructions`, which was **256** — generous for a scene typed
   out by hand and less than a third of the 850 the lattice compiles to. It is raised to 4096.
   That costs nothing anywhere else, and it is worth being clear why: unlike `MAX_SPANS` it
   sizes no shader array and creates no register pressure — iteration 7's wall is not this
   wall. It is a CPU-side sanity cap, and what now bounds a runaway scene is the iteration
   budget, which stops one before compilation ever sees it.

2. **`while` was never the interesting half of "bounded iteration".** The reason given above
   is that a file which loops forever produces no diagnostic. True, and incomplete: `for` is
   bounded and still admits `for (i in 0..1000000000)`, which fails in exactly the same
   unreportable way. Boundedness is not what makes a loop safe to run — a budget is — and it
   is the budget rather than the choice of `for` that closes the hole. (**Iteration 18 removed
   the budget and left the hole open.** The half of this that survives is the first half:
   `while` was never the interesting question. The half that does not is the conclusion, which
   assumed a number could be both a guard and generous enough for any real scene.)

3. **The unification is what made the iteration small.** Allowing `if` and `for` in a block
   *and* at the top level looked like the expensive requirement and was the opposite: making a
   block and a file the same statement list meant one implementation of each, and it dropped
   `let` inside a block out as a free consequence — which `lattice.chroma` then uses to name
   the position its four entries share. Two hierarchies would have cost two of everything and
   given nothing back.

---

## Iteration 9 — measured against the state of the art

**On standby** — parked before it started, not cancelled, and the section below stands as it was
written. The case for scheduling it here was that media and speed each have several defensible
algorithm families, and that choosing between them by reading once is cheaper than choosing
twice. That case is weaker than it looked: iterations 10 and 11 each open with their own
research pass, which is where those choices actually get made, and an audit of a renderer about
to gain a whole class of interaction would have to be run again afterwards. Run after iteration
11, it measures the renderer someone would actually use.

**What goes on standby with it**, named here because nothing downstream will surface it on its
own: item 3 below — whether resampled importance sampling retires the "a CSG solid cannot be
sampled" limitation inherited from iteration 4. That question is independent of both media and
speed, and it is the largest single limitation the renderer has.

**Deliverable.** `documents/state-of-the-art.md`: this renderer set against current production
and research path tracing, with every gap sorted into one of two piles — the ones that change
the image it converges to, and the ones that only change how long it takes to get there — and
at least one **number** from a reference renderer rather than a reading of the literature.

This is a documentation iteration, and iteration 0 is the precedent for one. The difference in
direction is worth naming: iterations 4, 5 and 0 wrote a reference document to specify what to
build. This one is written to find out what *was* built.

**Work.**

1. **A reference render, not an impression.** Rebuild `cornell.chroma` in PBRT v4 or Mitsuba 3
   and compare. Two traps to plan for. The comparison has to happen in **linear HDR before tone
   mapping**, since exposure and ACES can hide a real difference or invent one. And the material
   mapping is the delicate part — this renderer's metallic-roughness parameterisation has to be
   translated to the reference's, and a mismatch there reads exactly like a renderer bug. Expect
   a measurable deficit rather than a match: the fixed path length alone guarantees one, and
   iteration 5 already measured it at 6.3% on a specific patch at 8 bounces.

2. **The axes to score.** At minimum: light sampling and MIS; sampler quality — the PCG hash
   here against stratified, low-discrepancy or blue-noise sequences; path termination; the
   caustic strategy; spectral against three-channel; nested media; denoising. Each row says what
   the difference *looks like*, in the manner of transparency.md's Limits section. A gap nobody
   can see is not a gap worth paying to close.

3. **The one gap that is structural, and the reason this iteration is not just prose.**
   Iteration 4 established that a CSG solid cannot be sampled uniformly, concluded that emissive
   solids therefore cannot be reached by next-event estimation, and drew the honest corollary
   that MIS is unnecessary here. Resampled importance sampling does not need a uniform
   parameterisation — it generates candidates by any means at all and reweights them — and a
   bounding proxy per emissive solid is enough to generate them. If that holds, it retires the
   largest limitation this renderer has, and it un-retires MIS along with it. Research this one
   first and hardest; everything else on the list is a preference by comparison.

4. **Fix what "photorealism" is allowed to mean**, before the comparison, so the answer can be
   wrong. The proposal: the converged image is the one the rendering equation implies for the
   scene as described, to within measurable noise. That makes it a property with a reference
   rather than a matter of taste, and it puts everything that only affects the *route* to that
   image into iteration 11 by definition.

**Done when** the document exists, the reference comparison is a number and not a pair of
screenshots, and the gap table is ordered by what closing each row would cost. Two of those rows
should be recognisable as iterations 10 and 11. If they are not, the plan below is wrong, and
this is the iteration whose job is to find that out.

---

## Iteration 10 — participating media

**Deliverable.** `scenes/fog.chroma`: a shaft of light from a window falling through haze into a
room, and coloured smoke filling a spherical cavity cut out of a solid — fog that a `difference`
gives its shape.

Iteration 5 gave a solid an interior that **absorbs**: light crossing glass is attenuated over
the distance it travels and never leaves the straight line it arrived on. Scattering is the
other half — light that changes direction *inside* a volume rather than at a surface. It is the
first interaction in this renderer that does not happen on a surface, and the bounce loop's
central assumption, *trace to the nearest surface and shade there*, is precisely what it breaks.

**Research first — done.** [transparency.md](transparency.md#participating-media) gained a
participating-media section before any code, to the standard of iterations 4 and 5: the
radiative transfer equation, free-flight sampling with its weights derived rather than quoted,
the colour trap in sampling a three-channel σ<sub>t</sub>, Henyey–Greenstein with the sign
convention written down, next-event estimation from a vertex that has no normal, and four checks
that would catch a wrong implementation. It extends that document rather than starting a new one
because absorption was already there and is the same integral with a term missing.

Three things the research pass settled that this entry had guessed at:

- **The albedo is the whole of absorption.** Sampling the free flight from the transmittance
  makes the two cases weigh themselves — 1 for crossing, ρ = σ<sub>s</sub>/σ<sub>t</sub> for an
  interaction. There is no separate absorption step to write, and applying ρ again inside the
  next-event term is the double-count to watch for.
- **Coloured media need spectral care that this entry did not mention.** σ<sub>t</sub> has three
  channels and a free flight has one distance; sampling one channel and weighting with three is
  biased unless the pdf accounts for the choice. The fix is a balance heuristic over the three,
  and the symptom of skipping it is a hue error that grows with depth and reads as a wrong
  coefficient rather than as a sampling bug.
- **The transmittance shadow ray is a one-symbol change**, σ<sub>a</sub> → σ<sub>t</sub>, and
  only because the medium is homogeneous. That is the first thing heterogeneous density would
  cost.

**Where the interval algorithm pays for the third time.** A medium has to know where a ray is
inside which solid, and within one straight segment a span `[tIn, tOut]` **is** the integration
domain. So a medium is bounded by CSG for free: fog fills a `difference` cavity because the
cavity is where the spans are — no clipping geometry, no second representation of the boundary,
nothing to keep in sync. Note the limit correction 3 of iteration 5 established: this holds
per straight segment, and a ray that scatters starts a new segment and a new query.

**Work.**

1. **`scattering` beside `absorption`.** The material already carries `absorption`, which is σ_a
   — the absorption coefficient of a medium that happens not to scatter. Adding `scattering`
   (σ_s) and `anisotropy` completes a description that has been two thirds present since
   iteration 5, which is the argument for putting it on the material rather than in a POV-Ray
   `interior` block. Check it rather than assume it: a material is inherited through a parent,
   an interior is a property of one enclosed volume, and the two differ for a material used by
   two solids of different size.
2. **Free flight sampling**, homogeneous first: the distance to a scattering event is
   `-ln(1 - ξ) / σ_t` in closed form, and an event past the span's exit means the ray left
   unscattered. Heterogeneous density — procedural noise, delta or ratio tracking — needs
   nothing built here to be built later, which is the usual reason to leave it out.
3. **A phase function.**

   | Option | Cost |
   | --- | --- |
   | Isotropic only | One line, no parameter, no sampling routine. Fog then looks the same from every direction, and the light shaft — the deliverable — is weak, because a beam is forward scattering made visible |
   | Henyey–Greenstein | One parameter, one closed-form sample, one pdf. `g = 0` is the isotropic case exactly, so nothing is given up by taking it |

4. **Next-event estimation from a scattering event**, which is where the shaft actually comes
   from. A scattering point has no normal and no BRDF: the cosine disappears, the phase function
   replaces the BRDF, and the shadow ray starts inside a medium instead of offset from a
   surface. `shadowTransmittance` already walks occluders accumulating absorption and has to
   accumulate scattering extinction the same way. Without this, fog is a uniform veil and there
   is no beam in the image at all.
5. **The path budget stops being ignorable.** A path that crossed a glass sphere in two bounces
   can now spend all of `maxBounces` inside a cloud. The fixed path length is already a
   documented bias; media make it a *visible* one — dense fog will read as too dark, and the
   cause will not be the fog. This is the second reason to want iteration 11's Russian roulette,
   and the first reason it is not merely an optimisation.
6. **Register pressure, again.** Iteration 7 found the shader one step from the link wall, and a
   medium adds live state to the bounce loop. Measure the link early: the ceiling has been found
   by bisection twice, and both times it cost more than the feature that hit it.

**Done when** the shaft is visible with soft edges, fog confined to a CSG cavity stays inside
it, and — the regression that matters most — a material with `scattering: 0` reproduces
iteration 5's absorption **exactly**, because it is the same integral with one term set to zero.

**What was built.**

1. `scattering` and `anisotropy` on the material, through the binder, the model and the GPU
   table — which did not have to grow: iteration 5 left three floats spare beside `ior` and a
   medium needed two of them.
2. `phaseHg` and `samplePhaseHg` in GLSL, with the sign convention written into the shader
   beside them, and the free-flight sampling in the bounce loop.
3. `shadowTransmittance` carrying an extinction rather than an absorption, and taking the
   medium its origin already sits in.
4. `CompiledScene.HasMedia` and `uHasMedia`, so a scene with no medium keeps iteration 5's
   straight walk from surface to surface.
5. `scenes/fog.chroma`, and 9 more tests — 252 to 261.

**The estimator that came out of the research pass is not the one that went in.** The textbook
version samples the free flight from the full extinction σ<sub>t</sub> and weighs an
interaction by the albedo σ<sub>s</sub>/σ<sub>t</sub>. Sampling from **σ<sub>s</sub> alone** and
carrying absorption analytically is equally unbiased, and the same weight then falls out of
both branches: `exp(-sigma_a * distance travelled)`, which is the line iteration 5 already had.
Three things follow, and together they are why the roadmap entry above was rewritten rather
than followed:

- **The regression is a property of the algebra.** At σ<sub>s</sub> = 0 the sampled distance is
  infinite, the ray always reaches the surface, and the surviving weight is Beer–Lambert. There
  is no special case to write and no approximation to measure.
- **The spectral trap disappears.** The research pass devoted a section to sampling a
  three-channel σ<sub>t</sub> with a balance heuristic. Making `scattering` a single number
  removes the problem instead of solving it: only σ<sub>a</sub> is spectral, and σ<sub>a</sub>
  is never sampled from. The price is named — no Rayleigh scattering, so no blue sky — and
  coloured smoke, which is what the deliverable wanted, comes from absorption.
- **A strongly absorbing medium adds no variance of its own**, because absorption never
  terminates a path stochastically.

**The shader stopped linking, and the reason is iteration 7's.** A scattering vertex needs the
same next-event estimator with the cosine and the BRDF replaced by a phase function, which
reads as a second function beside `directLight`. It cost the whole feature: two functions call
`shadowTransmittance` from two places, the compiler inlines the tape walk into both, and the
program fails to link with `cannot locate suitable resource to bind variable`. Merging them
into one function was not enough — with `onSurface` a compile-time constant at each site the
compiler specialised the two copies straight back. What worked was restructuring the bounce
loop so there is exactly **one** call, with `onSurface` varying at run time. That is the same
constraint iteration 7 hit from the other side, and the same fix iteration 11 has listed.

**Verified.** `scenes/fog.chroma` shows all three claims: a shaft from the window with soft
edges landing as a bright pool on the floor, a ball of smoke with an octant cut away by a
`difference` and a hard edge where the cut is, and the haze itself carrying a spherical hole
cut out of it.

Four measurements rather than impressions:

- **`scattering: 0` is not merely close to iteration 5, it is identical.** `glass.chroma` at
  400 samples renders **byte for byte** the same across the change. `cornell.chroma` differs in
  **one channel of 2 764 800, by one part in 255**, which is floating-point reassociation from
  writing `brdf * cos` as one weight rather than four factors in a row.
- **Extinction really is σ<sub>a</sub> + σ<sub>s</sub>.** A medium of σ<sub>a</sub> = 1,
  σ<sub>s</sub> = 0 and one of σ<sub>a</sub> = 0.5, σ<sub>s</sub> = 0.5 produce **byte-identical
  images** of a shadowed floor at `maxBounces: 1`. Same optical depth, same picture; nothing in
  the transmittance can be reading the wrong coefficient.
- **An albedo-1 medium does not darken the room: −0.16%.** The first attempt at this check
  measured −13.5% and the check was wrong, not the code. A medium lives inside a
  `transmission: 1, ior: 1` solid, and crossing one of those costs two bounces whether or not
  anything scatters — iteration 5 measured that cost at 6.3% for a single sphere. Against an
  *inert* box of the same shape rather than against an empty room, the scattering medium costs
  0.16%, inside the ±1.5% these renders spread over at 500 samples.
- **The sign of `anisotropy` is right.** Looking along a beam through haze, `g = +0.8` is
  **1.68×** brighter over the frame than `g = -0.8`, and 225 against 176 in the region around
  the lamp. Nothing else in the implementation would have revealed a flipped convention.

**Found on the way.** Two scene faults, both of which produced images that looked like renderer
bugs and were not.

1. **A room whose walls merely touch is not sealed.** The first `fog.chroma` had the left wall
   stopping at the ceiling's underside rather than overlapping it. A point light finds a
   zero-width gap perfectly well, and the symptom was a broad wash of light on the far side of
   the room with a hard-edged shadow boundary — which reads exactly like a leak in the span
   algorithm. The shells now overlap at every corner.
2. **The smoke ball was inside the haze**, which is nested media, which the renderer does not
   support and does not report. It went unnoticed because the error is subtle rather than
   dramatic: crossing the inner solid replaces the outer medium and then leaves the ray in
   vacuum, so the haze simply stops existing behind the ball. The fix is the operator that was
   already there — the haze is now a `difference` with the ball's space subtracted from it —
   and it turned the scene into a better demonstration than it was before, since the medium is
   now shaped by CSG twice.

**Pulled forward from the manual's iteration.** `--samples <n>` renders to a stated sample
count, saves and closes. Iteration 10 cannot be *checked* without it: every claim above is a
measurement on a converged image, and a button in an overlay does not produce one reproducibly.
The window still opens — headless rendering is a different piece of work and stayed with the
manual, where it became iteration 13's first item.

---

## Iteration 11 — speed, at equal image

**Deliverable.** `cornell.chroma` and `glass.chroma` reach the image they converge to today in
half the wall-clock time, and the image they converge to is unchanged.

Deferring performance was right for seven iterations, and the reason it was right has expired.
The algorithm is settled and measured, and iteration 10 makes convergence time — not features —
the thing that decides what can be looked at.

**The rule.** *An optimisation is admissible if the image it converges to is the image it
converged to before.* That is testable rather than a sentiment, and it sorts every candidate:

| Class | What it changes | What has to be proved |
| --- | --- | --- |
| Less work per sample | nothing: the same estimator, computed faster | a converged render matching the old one to within measurement noise |
| Less noise per sample | the route, not the destination | that the estimator stays unbiased, plus the same converged comparison |
| Less noise than the samples justify | the image itself | out of scope by the rule — and if ever used, named the way `FIREFLY_CLAMP` is named |

**The metric has to change before the work starts.** Samples per second is what this roadmap has
quoted since iteration 6, and it is the wrong number here: a sampler that halves the variance
per sample is worth far more than a 10% sample-rate gain, and samples/s scores it as a *loss* if
it costs anything at all. The metric is time to reach a given error against a converged
reference, where the error is the relative standard error of the accumulated samples — which
needs the running variance alongside the running mean, and is the one piece of instrumentation
this iteration cannot be run without.

**Work.**

1. **Russian roulette, first, because it is not only a speed-up.** The fixed path length is a
   measured bias — 6.3% on the disc of iteration 5 at 8 bounces. Terminating paths stochastically
   with a compensating weight removes that bias *and* stops spending bounces on paths carrying
   almost nothing. It is the only item here that makes the image more correct while making it
   faster, so it goes first and the converged reference is taken after it, not before.
2. **A better sampler.** The PCG hash gives independent uniform samples per pixel per frame,
   which is the simplest correct choice and the noisiest one. Stratification, an Owen-scrambled
   Sobol sequence, or blue-noise-ordered seeds change nothing about the converged image and a
   great deal about how fast it arrives. Expect one specific failure: sampler correlation is
   *structured*, so a bad one does not look like noise, it looks like a pattern — and a pattern
   reads as a geometry bug.
3. **Bounding volumes per subtree.** A ray that misses a subtree's bound skips the branch. Class
   one, bit-identical output, listed under Beyond since iteration 0 and unchanged since; the only
   open question is whether the bound rides in the tape or in a parallel buffer.
4. **Stop inlining the tape walk twice.** Iteration 7 established that the compiler inlines the
   walk into both `trace` and `occluded`, which is what put `MAX_SPANS` one step from the link
   wall. Merging them into one parameterised call is a speed change *and* a capability change,
   and packing `Span`'s two surface codes into one int is the other half — together they are the
   route back to `MAX_SPANS` ≈ 12 and to the span budget iteration 7 had to clamp.
5. **Adaptive sampling.** The per-pixel error is already computed, so samples can go where the
   error is — the caustic in `glass.chroma` needs them and the flat wall behind it does not.
   Unbiased if the per-pixel sample count is carried into the average, subtly biased if it is
   not. This is the item on the list most likely to break the rule by accident, and the one to
   verify against a converged reference rather than by eye.
6. **What is deliberately absent.** Denoising and irradiance or photon caches are class three:
   they produce an image the samples do not support. Both are worth having one day and neither
   is what "faster without losing realism" asked for.

**Done when** both scenes hit the target, and their converged renders match the pre-optimisation
ones to the tolerance iteration 5 already achieved on `cornell.chroma` — 0.04%, which is a
measurement rather than an ambition because it has been reached once.

### What happened

**The equality is exact rather than within tolerance.** Every scene in the repository renders
**byte-identical** to its pre-optimisation PNG. The 0.04% allowance above was never spent.

**The target was all but reached, and passed everywhere else.** `cornell.chroma` and
`glass.chroma` came in at 1.73× and 1.74× against the 2× asked for. Every other scene met it or
beat it, and `lattice.chroma` — the one that prompted this iteration — is **10.58×**.
`documents/performance.md` carries the full table and the measured gain of each change.

**The plan had its order almost exactly backwards.** Item 1 was written as the one certain win
and is a net loss; item 4's second half was written as an afterthought to a capability change
and is the largest speed-up in this renderer's history.

| Item | Written as | Measured |
| --- | --- | --- |
| 1. Russian roulette | goes first, faster *and* more correct | net loss — removed |
| 2. A better sampler | changes nothing but how fast it arrives | 0.1% — removed |
| 3. Bounding volumes | bit-identical output, listed since iteration 0 | 8.4×, on one scene |
| 4a. One tape walk | speed *and* capability | parity; kept for the room it frees |
| 4b. Packing `Span` | "the other half" | **1.7–2.0× on everything** |
| 5. Adaptive sampling | most likely to break the rule | not attempted — see below |

**Why the roadmap misjudged it.** Every item above was reasoned about as instruction count, and
this shader is not bound by instructions. It is bound by how much state a thread carries: the
span stack is far too large for registers, so it lives in local memory and every tape
instruction reaches into it. That is why a struct one word narrower beat every algorithmic
change, and why *dead code* — a bounding-box branch `fog.chroma` never executed — cost that
scene a factor of 2.3 until it was put behind a `#if`.

**Two things the iteration added that were not on the list.** `--error <percent>` renders to a
stated noise level, which is the metric item 2 was to be judged by. And the trace shader is now
compiled *for the scene*: `CHROMA_TRANSMISSION`, `CHROMA_MEDIA` and `CHROMA_BOUNDS` replace two
uniforms and gate the guard branch, which is what makes item 3 shippable at all.

**Adaptive sampling is not done.** It is the only item that can change the image if got wrong —
it stays unbiased only if the per-pixel sample count is carried into the average, and the
accumulation buffer has nowhere to put one: RGB is the running mean and alpha the running mean
of the squared luminance, which the convergence meter needs. It needs a second render target
and a change to the buffer's layout, and it should be measured against the new baseline rather
than the one it was planned against — the flat wall it was meant to stop sampling is now 1.7×
cheaper to sample.

---

## Iteration 12 — per-scene code generation

**Taken out of order, and not from this list.** Iteration 11 established what this shader is
bound by — not instructions but how much state a thread carries — and the largest remaining
instance of that was structural: one shader compiled for every scene anyone might write, with
every array in it sized for the worst case. A scene of two spheres paid for the prism's crossing
array, the sweep's event arrays and a four-deep stack of eight-span lists.

The deliverable was that the tape interpreter goes away and every scene compiles to its own
GLSL, with one hard boundary: only the *geometry* is generated, and the path tracer — sampling,
BRDF, lights, media, accumulation, tone mapping — stays a hand-written file anyone can read.

It is written up in full where it belongs rather than restated here:

- [code-generation.md](code-generation.md) — what is generated, why the iteration-0 decision
  was reversed, and what the driver's instruction cap costs
- [performance.md](performance.md) — the measurements: **2.1× to 17.1×**, every image unchanged

Two consequences reach the rest of the documentation, and both are recorded where they bite:
`MAX_SPANS` no longer exists, so a primitive costs what it costs rather than being clamped at 8
([scene-primitives.md](scene-primitives.md#limits)); and a scene can
now generate more GLSL than a driver will link, which is a limit the manual's own gallery ran
into — see iteration 13.

---

## Iteration 13 — the illustrated manual

**Deliverable.** `documents/manual.md`: every feature of the language in the order someone meets
them, with a rendered image beside each example — and the images produced *from* those examples
by a script, so that a stale picture is not something anyone has to notice.

**One rule keeps it from rotting.** `scene-language.md` stays the reference: exhaustive,
normative, grammar and every field, no pictures. The manual is task-ordered, illustrated, and
never the place a field's meaning is defined — it links there. Two documents describing the same
thing at the same depth is how one of them quietly becomes wrong.

**Work.**

1. **Examples are real files** under `scenes/manual/`, quoted by the manual rather than typed
   into it. A snippet that has drifted out of the language then fails to load instead of
   misinforming a reader.
2. **The images are built, not collected**, which needs something that does not exist yet: the
   renderer opens a window and stops when it is closed, and sixty illustrations need a
   non-interactive path — a scene in, a PNG out at a stated sample count, nothing to close. The
   sampler is seeded from pixel and frame index, so a fixed count is reproducible frame for
   frame, and re-running the script is then a real diff rather than noise.
3. **`.gitignore` ignores `*.png` today**, so that renders made while using the tool never land
   in a commit. The illustrations need an explicit exception or the manual ships with no images
   and no error to say so.
4. **Size discipline.** Sixty illustrations at 1280×720 is tens of megabytes in a repository
   whose history is otherwise text. 640 wide is enough to show a shape and its shadow, and the
   sample count per image should be set by what that image needs — which the convergence
   measurement can answer per scene instead of a flat guess.
5. **A gallery**, which is the one page that argues for the project in ten seconds, and the thing
   the README cannot do today because it contains no images at all.

**Done when** every node and every field in `scene-language.md` appears in the manual with a
picture or a stated reason it has none; regenerating the images produces no diff; and a reader
who has never seen a `.chroma` file can get from the first example to a scene of their own
without opening the reference.

**What was built.**

1. **A non-interactive path**, as item 2 asked for: `--output <path>` writes exactly there
   rather than to the dated name in `renders/`, `--size <w>x<h>` sets the framebuffer, and
   `--headless` creates the window without ever mapping it to the screen — the ImGui controller
   and the overlay are skipped with it, since there is nothing left for an overlay to be drawn
   over. All three are refused without `--samples` or `--error`: a hidden window with no target
   never closes, which is iteration 1's rule about a loader that reports nothing, one layer up.
2. **Thirty example scenes** under `scenes/manual/`, each with a header comment saying what it
   is *for*, plus `palette.chroma`, which is a fragment and has no camera.
3. **`tools/build-manual.ps1`**, with three modes. The default renders every illustration and
   the gallery; `-Check` renders to a temporary directory and compares bytes against what is
   committed; `-Verify` loads every example through `Chroma.SceneDump` and checks that every
   fragment the manual quotes still appears verbatim in the file it claims to come from.
4. **`.gitignore` gained one exception**, `!documents/images/**/*.png`, and nothing else about
   the rule changed: a render made while using the tool still never lands in a commit.
5. **`documents/manual.md`** — ten task-ordered sections, thirty images, and a **coverage
   table** of every node and every field against the picture that shows it or the reason it has
   none. Five have a reason rather than a picture of their own: `camera.up`, a light's `color`,
   `blobSphere.strength`, `scale` and `exposure` — each of which either changes no pixel on its
   own or shows nothing a sentence does not.
6. **`documents/gallery.md`**, eight scenes that already existed, and the README's first
   images.

**Verified.**

- **The images are reproducible, which is the claim the whole approach rests on.** Two runs of
  the same scene at the same size and sample count are **byte-identical**, and `-Check` passes
  over all 38 images.
- **`-Verify` earned its place immediately.** It found a quoted `intensity: 3.2` in a scene that
  had been retuned to 1.9 while the manual was being written — exactly the drift item 1 was
  meant to prevent, caught by a command rather than by a reader.
- **Weight: 5.9 MB for 38 images** at 640×360, against the "tens of megabytes" item 4 warned
  about. Sample counts are per scene and were chosen with `--error`; they range from 1500 for a
  sphere on a plane to 20 000 for the emissive room and `glass.chroma`, which is the honest
  price of a source that is found by chance rather than sampled.
- **Every illustration was looked at**, and four were rebuilt because the picture did not show
  what the text beside it claimed.

**Found on the way.**

1. **The gallery is the first thing that ever rendered every showcase scene in one command, and
   it found one that no longer renders.** `chess-full.chroma` generated 7434 lines of GLSL and
   the driver refused the program: roughly 65 000 assembly instructions is the cap, and the
   compute path has the same one. That was iteration 12's ceiling, met by a scene that was
   written to find edges. The gallery used `chess-half.chroma` at 6436 lines, and said so rather
   than quietly choosing the one that works. Iteration 14 made the full set compile.
2. **Two of the three "one number apart" pairs were rewritten after seeing them.** The first
   `light-radius` scene tried to show a hard shadow beside a soft one by putting two lights in
   one room; lights are global, so every occluder had two shadows and neither claim was legible.
   A pair of files differing in one number is what works, and it became the manual's idiom —
   `fov`, `radius` and `anisotropy` all use it.
3. **The anisotropy claim in the roadmap and in `fog.chroma`'s comment is overstated, and the
   image says so.** "At 0 a medium is a uniform veil from every direction" implies there is no
   shaft at all without forward scattering. There is: the window bounds the lit volume, and that
   alone gives it edges. What `anisotropy` buys is contrast — a sharper beam against a dimmer
   room — not the beam's existence. The manual says the measured thing.
4. **Tone mapping, not lighting, is what made the first dozen renders look wrong.** They came
   out pale and washed out at intensities that seemed reasonable, and the fix was `exposure`
   rather than the lights. Worth recording because the symptom — colours drifting towards white,
   all of them, evenly — reads like a material bug and is a scene-settings one.

---

## Iteration 13 — the driver's instruction ceiling

Per-scene code generation replaced one limit with another. The array sizes are gone; what a
scene then ran into is how much code the driver will compile into one program, about 65,000
assembly instructions, which `scenes/chess-full.chroma` reached. That scene was kept in the
repository precisely because it did not compile; `scenes/chess-half.chroma` is the same set cut
to a sixteen-man position that fitted. [gpu-backends.md](gpu-backends.md) records the limit, the six things tried against it,
and what each one measured. Iteration 14 below is the seventh, and the one that worked.

**Done.** The register-pressure wall that came first (`error C5041`, several hundred of them) is
gone for good: span lists are a reused pool of file-scope globals, leaf scratch is shared, no
array is ever a function parameter, and the sorts no longer unroll. The renderer now negotiates
an OpenGL 4.6 context and can run the tracer as a compute shader over storage buffers, from one
shader body compiled as either stage. Leaf bodies are shared between identical solids.

**Measured and negative.** Neither a newer GLSL version nor the compute stage lifts the ceiling:
the same scene is refused one instruction apart on both. The compute path is also not faster
overall and is 3.5x slower on the scene with the heaviest register load, so it is opt-in behind
`--compute`. Sharing leaf bodies cut the source 29% and the ceiling by almost nothing, because
the inliner puts the copies back.

## Iteration 14: instancing, and the ceiling moves

`chess-full.chroma` compiles and renders. The compiler works out which roots are the same solid
standing somewhere else, emits one body for each distinct shape, and puts the placements in a
buffer with a BVH over them; the walk over that tree is bounded by a uniform, which is the one
thing the driver cannot unroll. 162 primitives became 32, a hundred-odd root bodies became 10
shapes, 7,434 generated lines became 3,342. What a scene costs is now how much geometry it holds
that is *different*, and repeating a piece is free.

**The language did not change**, which was the constraint. A `let` still stores an unbound block
and a `function` still returns a fresh tree per call, so nothing in the model says two pieces are
the same. Shape identity is *recovered*: a root is peeled of its placement and emitted into a
throwaway emitter, and two roots are the same shape when they emit the same GLSL. That makes the
comparison exact and impossible to drift from what is actually generated, since what a solid *is*
is defined as what it compiles to.

**Measured.** chess 5.79x, lattice 3.43x, chess-half 3.04x, and neutral everywhere else. The gain
is the tree rather than the sharing: `traceScene` used to test every root's box in source order.

**The threshold, which was not the plan.** Sharing everything shareable cost `glass` 35% and
`cornell` 18%, because a BVH walk is a loop of dependent memory reads where a run of folded guards
is independent work. So a scene shares nothing until it holds 32 repeated placements, and a driver
refusal overrides that, since a program that will not compile has no speed to protect. Eleven of
fourteen scenes are under the threshold and render bit-identically to iteration 13.

**One prediction was wrong**, recorded because it was load-bearing in the plan: the packed `surf`
encoding was expected to have to name *(instance, leaf)*. It did not. The walk that chooses an
instance is the walk that folds its span list in, so it says which one it chose, and the largest
single speed-up in this renderer's history was not touched.

**Found on the way.** Two bugs, both in the folded path and both invisible to the unit tests that
existed. A shape recognised as shared but left below the threshold was emitted from the group's
tree rather than its own, so every appearance was drawn on top of the first. A cornell render is
what found it, with the ceiling and one wall missing. And the material *slot pattern* was masked
out of the shape key, so two solids of the same geometry differing in how their materials repeat
compared equal. What both point at is that the tests checked mechanisms and not outcomes; the test
that now covers them compiles every scene twice and asserts no solid moved.

**Next, if the ceiling is worth raising further.** It now falls on *distinct* shapes, so a scene
of two hundred different turned pieces would still be refused, and the refusal still names lines
rather than the shapes that cost the most. A cost model per shape would fix the message and let
the fold threshold be driven by the budget rather than by a count. Below that: SPIR-V, cheap to
try and now worth even less; and wavefront rendering, which removes the ceiling rather than moving
it. Instancing is what makes its chunks definable, since a chunk is a set of shapes and its own
tree over their placements.

---

## Iteration 15 — a cost model, and the ceiling goes

Both halves of the paragraph above, built in that order because the second needs the first: a chunk
is *defined* as a set of shapes that fits a budget, and there is no budget without a cost model.

`scenes/palisade.chroma` is the artifact, and it is this iteration's `chess-full`: two hundred
hexagonal posts of two hundred different sizes, which no single program will take and which now
renders. The compiler splits its geometry in two, compiles a program per chunk, and traces the path
one stage at a time over ray state in buffers, keeping the nearest hit across the passes. No flag,
nothing in the scene file.

**The cost model is counted, not written down.** `GlslWriter` totals statements as the emitter
writes them, so a shape's cost is a property of what it emits in exactly the sense its identity
already was, and the same `Probe` that computes the shape signature reports it — which makes the
number the partition decides on, by construction, the number the emitter goes on to produce.
`SceneCompiler` throws if they ever disagree. Three things about the count are counter-intuitive
and all three are load-bearing: a shared leaf body still costs once per *call*, because the driver
inlines; a loop bounded by a literal costs its trip count; and a CSG operator costs a *constant*
rather than its span width, because every loop inside it is bounded by a runtime `count`.

**The path tracer came apart before anything else did.** `pathTrace` became `spawnPath`,
`intersectPath`, `shadeVertex`, `bouncePath`, `connectDirect` over an explicit `PathState`, with
the megakernel as their composition — and that step had to be byte-identical on its own before a
buffer existed anywhere, which it was on all 17 scenes. The wavefront then runs the same five
functions over state in a buffer. There is one path tracer in the file, not two.

**Measured, and the prediction was wrong.** The wavefront was written up in advance as slower on
every scene that fits, since one sample becomes tens of full-resolution dispatches with no
compaction. It is 1.44x *faster* on `chess-full` and 1.17x faster on `sweeps`, and slower on the
light scenes. The two that gain are the two already recorded in performance.md as register bound —
`sweeps` being the scene measured at 0.29x on the compute path for that exact reason — and cutting
what one kernel holds live is the classic reason a wavefront helps. The rule is not "slower"; it is
that dispatch count is traded for occupancy, and scenes that need splitting are on the side that
gains.

**The calibration sweep measured its own base.** `tools/measure-shape-cost.ps1` brackets, per shape
kind, the largest scene the driver will take. Cheap kinds were measured on top of 55 lathes so that
a bracket could be found without generating four thousand spheres — and that base is 39,490 of the
~42,000 statements involved, so six of the eight cases were mostly measuring the base. They agreed
within 4%, which read as a result and was an artifact. The two cases with no base refuse at a
*third* of that: 111 prisms will not compile where 55 lathes and 67 spheres will, though the model
costs the second at three times the first. So the weights are wrong between kinds by about 3x,
`ShapeCost.Budget` is still a placeholder, and the machinery is right while the calibration is not.
Recording this rather than fitting a number to it is the whole point of having stated the error.

**There are two ceilings.** `too many instructions` is the tidy one. `error C5041: cannot locate
suitable resource to bind variable` is the register ceiling — the same failure iteration 7 met from
the other side — and which one a scene reaches depends on what it is made of, not on how far over
it is. `GlCapabilities.IsOverflow` did not recognise C5041 until the sweep produced one, so such a
scene skipped the retry entirely and showed the reader two hundred lines of driver log.

**Found on the way.** A small scene can wedge the driver's compiler: thirty-two six-point lathes,
12,224 statements, did not finish in fifteen minutes and 1,707 seconds of CPU, and had to be
killed. Sixteen of the same compile normally. The sweep was scoring that as a refusal by timeout,
which is how a compiler bug gets recorded as a capacity measurement.

**One real bug, and what caught it.** The wavefront's shadow walk rebuilt `inMedium` at each step
instead of carrying it across. In vacuum that is invisible, because `exp(-0 t)` is 1 however often
it is applied; inside fog it is wrong by a mean of 12/255 across three quarters of the frame.
Nothing would have found it by inspection. What found it was insisting on byte-identity between the
two drivers: exactly three scenes differed, they were exactly the three compiled with
`CHROMA_MEDIA 1`, and that was the whole diagnosis. Demanding equality rather than a tolerance is
what turned a subtle physical error into a one-line search.

**Verified.** Three byte-identity sweeps of 17 scenes each — the megakernel before the stage split
against after, the megakernel against the wavefront at one chunk, and one chunk against two to six
chunks at a held partition. The third needs the partition held still to mean anything, since a
budget low enough to force splitting also makes the partition share more, and sharing legitimately
changes the last bits; `--budget` exists for that comparison and for nothing else.

**Next.** Fixing the cost model's weights, which is what makes the budget mean something.
Compaction, which is where the wavefront gets the rest of its speed. And the question deliberately
left closed: a chunk cuts between whole shapes, so `scenes/cube.chroma` — eight thousand boxes in
one `union`, one shape with eight thousand leaves — is still refused. Cutting inside a top-level
`union` would render it and is sound for nearest-hit, at the cost of two overlapping transmissive
children in different chunks no longer coalescing into one interval. That is the same limitation
"top-level solids are unioned but not merged" already documents, and it is not a thing to start
doing silently.

## Iteration 16: the silence before the first image

`scenes/cube.chroma` spends over two minutes with a busy CPU, nothing on screen and not one line of
output, and then fails. Finding out what that time actually is came first, and the answer moved
where the work went.

**It is almost none of this program.** Reading the scene, recovering its shapes and generating its
157,628 lines of GLSL is **0.55 s**. The other 149 s is inside `glCompileShader`, and there is
nothing on this side of that call to make faster.

**Compile time is not proportional to program size, and the knee is far below the ceiling.**
`chess-full` sits at a quarter of the instruction budget, generates 3,342 lines, and takes **159 s**
the first time it is ever compiled and **0.5 s** every time after, because the driver keeps what it
compiled in a cache of its own. `cube.chroma` gets no such relief: a driver caches what it compiled
and never what it *refused*, so a scene that will not fit pays its two minutes on every single run.
That asymmetry is the whole of the complaint, and it also makes the earlier finding that "a small
scene can wedge the driver's compiler" look less like a hang and more like the same steep curve
seen further along. Nobody has re-measured those thirty-two lathes, so it stays an open question
rather than a settled one.

**A scene compiled as several programs now compiles them at once**, which took three separate
things being true and only became visible when all three were. Every stage has to be handed over
before anything is linked; every program linked before anything is asked about, since asking is
what waits; and, the one that is in no tutorial, the driver has to be *told* it may use threads. `GL_ARB_parallel_shader_compile` states that `MAX_SHADER_COMPILER_THREADS_ARB` starts at
the implementation maximum, which reads as though there is nothing to do. With the extension
present, the completion query answering and the first two conditions met, a ten-chunk `palisade`
still compiled its fifteen programs strictly end to end at 11.1 s. One call to
`glMaxShaderCompilerThreadsARB` took it to **3.6 s**. It is worth nothing on a warm cache and
nothing at all on a scene with one program, which is every scene that is not chunked.

**Nothing waits in silence any more.** A step that outlasts one second counts itself out on stderr
and erases the line when it is done: the scene compile, the driver compile, and each of a
wavefront's programs as it comes back. One second of grace is what keeps every fast scene, and all
sixty of the manual's renders, looking exactly as they did.

**Deliberately not done.** The estimate is not allowed to refuse a scene. `cube.chroma` is at 1360%
of the budget and its single shape alone accounts for all of it, so no amount of sharing or
splitting can help and a refusal could be predicted in 0.6 s instead of paid for in 135 s. It is
still handed to the driver, because the driver is the authority and the cost model is known to be
wrong between shape kinds by about 3x. That is a decision, not an oversight.

**Next, and it reverses a decision made twice.** A chunk cuts between whole shapes, which is why
`cube.chroma` is refused rather than split; both iteration 15 and `SceneChunker`'s own remarks
argue for leaving it that way. It is now the thing to do. Eight thousand boxes in one `union` is one
shape with eight thousand leaves, and until a chunk can cut *inside* a top-level `union` there is a
class of scene that no amount of instancing, sharing or splitting will render, which is a hole in
the claim that the ceiling is gone. What has to be faced is what iteration 15 wrote down: two
overlapping *transmissive* children landing in different chunks stop coalescing into one interval,
so the cut is sound for nearest-hit and changes the picture for glass that overlaps glass. The
shape of the answer is probably to cut freely where the operand subtrees are opaque or disjoint and
to refuse to cut across an overlapping transmissive pair, which is a test on the operands rather
than a blanket rule. It also needs the span-list widths to come down with the cut: `cube.chroma`'s
root is 8,000 spans wide, and a chunk that still declares that has not been made smaller in the way
that matters.

## Iteration 17: cutting inside a top-level `union`

The scene the renderer could not draw, drawn. `scenes/cube.chroma` went from 1360% of the
instruction budget, 157,628 generated lines and about 140 seconds of driver time ending in
`fatal error C9999` to **3% of the budget, 1,626 lines, roughly a second to compile and 110.9
samples/s at 1280x720**. The full write-up, with the round-by-round table, is
[cutting-unions.md](cutting-unions.md).

**It was not a chunking change.** Iteration 16 left this as "a chunk should be able to cut inside a
shape", and that framing was wrong in a way worth recording. A scene's roots are *already* an
implicit union that is resolved one at a time: `GeometryEmitter.EmitShape` gives each root its own
function, its own bounds test and its own span list. So cutting a `union` root into one root per
operand says the thing exactly, in terms the compiler already speaks, and needs no new machinery in
`SceneChunker` at all.

**And the cut is not what makes the scene smaller.** The pieces go back to `ShapeCanonicalizer`,
which then sees what it never could: the twenty sub-cubes of `cube(3)` are one shape standing in
twenty places. Cutting exposes repetition that was locked inside a single shape, and instancing
collapses it. `cube.chroma` ends as **one** program holding one body of twenty boxes, reached from a
400-instance BVH. Spreading it over more programs was never the answer and never happens.

**Both caveats iteration 15 wrote down held, and both are in the code.** Two overlapping
*transmissive* operands stop coalescing into one interval when separated, so `RootSplitter.Cuttable`
declines that pair specifically, testing the operands rather than the union; bounds are asked for
only when two operands could be transmissive, so an opaque scene pays one material walk. And the
span width had to come down with the cut, which the estimate cannot see: `cube.chroma` cut *once*
already fits the budget at 68% while declaring a 400-span list per thread, about six kilobytes of
state where the widest root anywhere else in the repository is `sweeps` at 24. `ShapeCost.MaxSpans`
is the second stopping condition, set to 32 so that it clears every existing scene with room, and it
is a target for cutting rather than a limit a scene can fail: a forty-segment `lathe` is one leaf,
has no seam, and compiles as it always did.

**Cutting is a last resort and stays one.** A scene inside the budget returns from the first line of
`RootSplitter.Cut` untouched, so every scene that compiles today is byte-for-byte what it was as a
matter of construction rather than of measurement. Nor is a scene over budget only *in aggregate*
cut: that is what chunking is for, and a chunk costs a second pass of the path tracer where a cut
costs coalescing. `palisade.chroma`, two hundred posts of which none is near the budget, goes
through untouched and is chunked exactly as before.

**One decision reads better in hindsight.** Iteration 16 deliberately refused to let the estimate
reject a scene, on the grounds that the driver is the authority. The stated reason was the cost
model's 3x error between shape kinds. The better reason turned out to be that `cube.chroma` was
never hopeless: the compiler was. An estimate allowed to refuse would have made that harder to find.

**Verified.** 422 tests. `cornell`, `chess-full`, `glass` and `palisade` rendered before and after
and compared byte for byte, all four identical; `build-manual.ps1 -Check` reporting the same four
pre-existing differences and no more; `cube.chroma` byte-identical between the compute and wavefront
paths, and all eight thousand of its boxes standing where they stand when the scene is compiled
whole, compared through the scene-wide tables by `RootSplittingTests.Cutting_never_moves_a_solid`.

**Next.** Fixing the cost model's weights, still. Compaction in the wavefront. And the question this
iteration opened rather than closed: cutting stops as soon as the width rule is satisfied, so
`cube.chroma` ends at four hundred appearances of a twenty-leaf shape rather than eight thousand of
a one-leaf box, and nobody has measured which of those renders faster.

## Iteration 18: the loader stops counting

**One scene, and one constant that outlived its argument.** `scenes/cube-4.chroma` is
`cube.chroma` one level deeper: 160,000 boxes instead of 8,000. It was refused before anything
was compiled, at `scenes/cube-4.chroma:11:21`, because building it costs 328,419 loop iterations
and 168,421 function calls against budgets of 100,000 each.

Iteration 8 wrote the budget's justification into the decision table itself: *"no scene worth
writing comes near it, the lattice spends 125."* That was true when the ceiling on a scene was
the instruction tape. Iterations 14 through 17 moved the ceiling three times, and this scene is
now on the other side of it: cut and instanced, it is **one shape of twenty leaves standing in
eight thousand places**, which is 1,626 generated lines and 3% of the instruction budget. The
budget was not protecting the renderer from the scene. It was the only thing refusing it.

**So the count is gone**, rather than raised. A number large enough for `cube(4)` guards against
nothing, and a number small enough to guard refuses scenes that render, so there is no number.
`MaxLoopIterations` and `MaxFunctionCalls` are deleted, with the two fields, the two
"reported once" flags and the two diagnostics.

**What that costs, stated plainly.** `for (;;) { sphere { } }` now runs until memory is gone, and
`tree(40)` makes 2^40 calls and never comes back. Neither reports anything: no diagnostic, no
window, no exit code. That is precisely the failure iteration 8 introduced the budget to prevent,
and it is now accepted, on the grounds that no interpreter caps its user's loop count, the
non-termination belongs to the file rather than to the loader, and a console renderer can be
interrupted.

**`MaxCallDepth` stays at 64**, and the argument for it is narrower than the one it used to share.
A loop that never ends can be interrupted. A recursion that overflows the CLR stack cannot be:
`StackOverflowException` cannot be caught, cannot be reported, and takes the process with it.
That is a different failure and it keeps its guard.

**What `cube(4)` actually costs.** It emits the same program as `cube.chroma`, identical down to
two comment lines that say 8,000 placements instead of 400, so the driver's side of it is
unchanged. Everything the extra level costs is on this side:

| | `cube.chroma` | `cube-4.chroma` |
| --- | --- | --- |
| boxes written | 8,000 | 160,000 |
| shapes, placements | 1, 400 | 1, **8,000** |
| generated lines, estimate | 1,626, 3% | 1,626, 3% |
| widest root | 20 spans | 20 spans |
| launch to first frame | 1.1 s | **9.7 s** |
| peak memory | 182 MB | **2.3 GB** |
| 1280x720 | 108.6 samples/s | 38.6 samples/s |

The 2.3 GB is the number worth keeping. Four cut rounds each re-probe every root, and a probe
emits, so the front end asks for something like 1.3 million leaves' worth of GLSL before the
driver is called once. That is a finding rather than a defect this iteration set out to fix, and
it is the first time the loader rather than the driver has been the expensive half of a run.

**What was built.** The removal above; `scenes/cube-4.chroma`, named in both repository sweeps'
exclusion lists for what it costs to compile rather than for anything it fails; and three tests
deleted, `Refuses_a_loop_that_would_run_away`, `The_iteration_budget_is_shared_across_a_whole_load`
and `Refuses_a_recursion_that_branches_faster_than_it_ends`, each of which asserted behaviour that
no longer exists. `Refuses_a_recursion_that_never_ends` is now the only test of the only guard
left.

**Verified.** 419 tests. `cornell`, `chess-full`, `glass`, `palisade` and `cube` rendered before
and after the change and compared byte for byte; `build-manual.ps1 -Check` reporting the same four
pre-existing differences and no more. `for (;;) { }` checked by hand to run rather than report,
since there is deliberately no test that can assert it.

**Next.** The loader's memory, if a scene bigger than this one is ever wanted: the cut rounds
re-probe from scratch, and the first round probes a tree it already knows it is about to cut
apart. And still the question iteration 17 left: nobody has measured whether eight thousand
placements of a twenty-leaf shape beat one hundred and sixty thousand of a one-leaf box.

---

## Iteration 19: randomness, and the rest of C's operators

Two entries off [suggestion.md](suggestion.md), taken together because the first one needs a
decision the second one does not affect and the third one does: `random` is the language's
**first built-in function**, so whatever shipped it also settled how a built-in is named, scoped
and refused, for `sin` and `floor` and everything after them.

**`random(i)`, and `perlin(x, y)` beside it.** Both are drawn **while the scene is being
built**, on the CPU, by the evaluator. The result is an ordinary number in a field, and by the
time anything is compiled there is no randomness left anywhere — the shader neither knows nor
could know that a radius was drawn rather than typed. That is the opposite side of the compiler
from the per-pixel PCG hash the shader has had since iteration 4, which draws a fresh number
every sample on purpose; the two share the word and nothing else.

**A hash, not a stream**, which was the decision the entry named and the one everything else
follows from. `random()` returning the next value of a stream would make every result depend on
the order the evaluator happens to walk the tree, so a refactor of `Evaluator` would silently
redraw every scene that used it and no test would name the cause. `random(i)` has no order to
depend on: the scene supplies what varies, usually the loop counter, and the same argument gives
the same number wherever it is written. It ships in **one form**, a number in `[0, 1)`, because
`lo + random(i) * (hi - lo)` is the range and the language already has the arithmetic.

`perlin` is that same answer with one property added — neighbouring inputs give neighbouring
outputs — and three choices stated rather than left implied. **Two dimensions**, because terrain
is what a scene file asks noise for and a solid texture belongs on the other side of the
compiler. **One octave**, because fractal summation is a four-line loop in a language that has
loops, and putting octaves, lacunarity and persistence inside the built-in would hide the one
thing it should show. **The same seed**, so that determinism is one property of the language
rather than one per function.

**The seed is a scene field, and it cost the one thing the entry did not predict.** `render {
seed: 7 }` beside `maxBounces` and `exposure` is right, and it is also read *after* the whole
file has run — by which time every number that needed it has been drawn. So it is read twice: a
pass over the parsed file lifts it out of the **text** before evaluation begins, and the
`render` binder reads it again as an ordinary field. `SceneBuilder` compares the two and reports
the difference, which is one check covering both ways they can part company — a seed written as
an expression, which the early pass cannot evaluate, and a `render` block in an included
fragment, which the early pass never sees. The rule a scene sees is one line: **a plain number,
in the scene file itself.**

**Determinism is the feature rather than the caveat.** Absent, the seed is `0` — fixed, never a
clock and never a process id. The same file gives the same numbers on another machine, which
rules out any generator with a platform-dependent step, the framework's own `Random`, and any
use of `sin` or `cos`: the mixing is SplitMix64's finaliser on a `ulong`, and the noise is the
four IEEE 754 operations that are correctly rounded and a table of eight unit gradients written
out as literals. Three things rested on a scene loading to the same bytes twice — the manual's
`-Check`, the dump comparisons, and iteration 15's byte-identity sweeps — and all three still do.

**A built-in is a binding in a frame outside the file**, which is what makes it cost no new
rule. Lookup finds it last, so nothing local is slowed or shadowed by one; an `include` runs
against a frame of its own over the same built-ins, so a fragment sees exactly what a scene file
sees; and `Scope`'s no-shadowing rule refuses `function random(i)` in a scene rather than letting
it quietly win. The refusal names a **built-in**, not a definition: the frame it collides with is
not in the file, and "already defined" would send a reader looking for a declaration that is not
there. The frame is built per load, because the seed is captured in it.

**And the rest of C's operator set.** `^` was the gap a scene met in practice — "exactly one of
these" had to be written `(a || b) && !(a && b)` — and `&`, `|`, `~`, `<<` and `>>` were the
question the entry said was worth deciding rather than leaving to the parser. The decision:
**they exist, and they refuse a fractional operand by name.** `&`, `|` and `^` carry both of C's
readings, chosen by their operands — two booleans give the logical connective without the short
circuit, two whole numbers the bitwise one — and nothing mixes the kinds, so one spelling serves
both with no ambiguity to resolve. `~` is the numeric complement beside `!`'s boolean one.

**The precedence table is C's, whole**, including the two places C's is inconvenient: a shift
binds looser than `+`, and the three connectives bind looser than `==`, so `x & 1 == 0` reads as
`x & (1 == 0)`. Both are kept, and the second is an *error* here rather than a wrong number,
because the type rule catches what the precedence rule sets up. A scene written by someone who
knows C must not quietly mean something else, and inventing a second table for one language is
worse than inheriting a known one. Nine test cases pin the ladder one rung at a time, each
written so that the other reading gives a different answer. Associativity is pinned only where
it can be observed: `&`, `^` and `|` are associative, so no scene can tell which way they
grouped and a test asserting it would pass either way.

**Whole numbers are a constraint, not a type**, which is the choice `BlockReader.Integer` already
made for a field: the language has one numeric kind and it is a 64-bit float, so `1.5 & 1` is
reported rather than truncated. The magnitude limit is 2^53, where a double stops holding every
whole number, and it is checked on the operands and again on the result of a `<<` — the one
operator that can carry two operands in range out of it. Shift counts outside `0..63` and
vectors are refused by name; `>>` keeps the sign.

**What was built.** `Ampersand`, `Pipe`, `Caret`, `Tilde`, `LessLess` and `GreaterGreater` in
the lexer, and the lone-`&`/`|` near-miss hint deleted with the reading that made it one; five
`BinaryOperator`s, one `UnaryOperator`, and three new precedence levels in `Parser`;
`EvaluateBitwise` and `EvaluateShift` in `Evaluator`; `BuiltinValue`, `Builtins`, `SceneNoise`
and `SeedReader`; `RenderSettings.Seed` and its field; a `seed` column on the hierarchy dump's
`Render` line.

**Verified.** 492 tests, of which 74 cover this iteration: every rung of the precedence ladder, both readings
of each connective, every refusal, the unit interval, the stream property, byte-identical
repeat loads, coherence across neighbouring `perlin` inputs, the built-in collisions, and the
two ways a seed can disagree with itself. `build-manual.ps1 -Verify` reporting every scene
loading and every quotation matching. The change is **additive**: no scene file was touched, and
nothing that parsed before parses differently — a lone `&` or `|` was an error before and is an
operator now, which is the only reading that changed.

**Next.** The rest of the maths entry below is now one decision short of free: `PI`, radians,
and the function library all land in `Builtins` as one line each, and the naming and scoping
question they shared is answered. And `scenes/palisade.chroma` is still two hundred posts
written out by hand — five lines now, if anyone is willing to spend the byte-identity of its
image to prove it.

---

## Iteration 20: arrays, structs, and the maths that needed them

Three entries off [suggestion.md](suggestion.md), and they are one iteration because the third
one cannot be finished without the first. `length`, `normalize`, `dot` and `cross` were recorded
as *missing from the language* rather than as functions nobody had written: there was no way to
hand a vector to a function or get one back, because the built-in signature was numbers in and
one number out, and the value model had nothing wider to offer it.

**Arrays are the language's vector, widened rather than joined by a second kind.** A vector was
a flat list of numbers that could not nest, which [scene-language.md](scene-language.md)
recorded as a limitation, and `prism` and `lathe` worked around it by interleaving:
`[x0, z0, x1, z1, …]`, paired up by the node. Making the elements arbitrary values costs no new
syntax and no conversion: an array whose elements are all numbers *is* that vector, with the
same arithmetic and the same broadcasting, so every scene written before this means exactly
what it meant. The two words now describe one type from two ends, and `Describe()` picks
between them by content: a list of numbers is *a vector of 3 components* wherever a field wants
one, and anything else is *an array of 2 elements*. Every diagnostic written before this says
what it said.

They hold anything and nest to any depth: numbers, strings, other arrays, structs, whole nodes.
`a[i]` reads an element and `a.length` counts them, a member rather than a built-in, because
`a.length` is the spelling a reader arrives with and because `length` as a *name* was already
spoken for by the vector magnitude in the same entry.

**Structs are a record type, not an object literal that happens to have the right keys.** That
distinction is the whole of what the declaration buys, and it shows up as diagnostics: a
missing field, a misspelt one and a duplicate are each reported where the instance is written
rather than wherever the value was eventually needed, and several missing fields are listed in
one message rather than three. An instance is written with the block syntax that already
exists, `Point { x: 1, y: 2 }`, so the parser needed nothing for it. Which of the two a block
is comes from what its name resolves to, a struct type being a binding and a node name being
nothing at all until a binder looks it up. The corollary is that `struct sphere { … }` is
refused at the declaration, which is the one place the evaluator is told what the binders know.

**The four questions the entry listed, answered.**

1. **Immutable, both of them.** `p.x = 3` and `a[0] = x` are refused by name, with a message
   that says why rather than one about an unexpected `=`. `let` became mutable with the C-style
   loop because a counter changes; a record does not have to, and leaving both immutable means
   `let q = p;` raises no question about whether it copied or shared. That is the question this
   language already answered the other way for solids, where referencing a binding twice
   instantiates it twice. Mutability can be added later; it cannot be taken away.
2. **`==` compares arrays element by element and structs of the same type field by field**,
   recursively. Two arrays of *different* lengths are unequal rather than incomparable, since they
   are the same kind, and a length is a fact about a value rather than about its type. Two
   different struct types are **not** comparable even if their fields match, which is reported:
   two types with the same field names are still two types, and that is the reading a declared
   record type is for.
3. **Arithmetic is refused by name**, as it already was for strings and booleans. A struct
   reports *'Point' structs do not support arithmetic*. An array that nests is refused as a
   whole rather than element by element: a list of points is not a quantity, and a deeper
   broadcast is not the answer anyone wanted.
4. **The hierarchy dump is unchanged.** Neither kind reaches the scene model: a struct is read
   for its fields and an array for its elements long before a binder sees a number. `prism`,
   `lathe` and `sphereSweep` accept the paired form *and* the flat one, and both arrive at the
   binder as the same run of numbers, so a scene rewritten in points dumps byte for byte what
   it dumped interleaved.

**One ambiguity had to be closed rather than documented.** Commas are optional everywhere here,
so a naive postfix `[` reads `[[0, 0] [1, 0]]` as one array indexed by another, and a
`sphere { }` followed by `[1, 2, 3]` on the next line as one indexing expression. JavaScript
has the same hole and closes it with a newline rule; whitespace is insignificant here and
buying that rule would make it significant everywhere. So `[` indexes only after something that
could *name* an array: an identifier, a call, an index or a field. Nobody indexes a
literal. `.` needs no such guard: it cannot begin a statement or an expression.

**`PI`, the library, and a radian mode.** All of it, as the entry listed it, plus the decision
it left open: **trigonometry is in radians unconditionally**, and `render { angles: … }` covers
the two angular *fields* it said it covered and nothing else. That split is what makes the mode
implementable at all, since the built-ins are created before evaluation and the `render` block is
bound long after, and it is also right on its own terms: `sin` is mathematics, and `PI` is
what makes radians usable in a file that types its angles in degrees. `round` goes away from
zero at a half, as C's does and .NET's does not; `log` is natural, as C's is. A domain error
answers `NaN` rather than reporting, because `1 / 0` has produced infinity here since the
language had arithmetic and checking `sqrt` while leaving `/` alone would be an inconsistency
rather than a safety net. The three cases with no number at all to give back are reported: `normalize` of a
zero vector, `dot` of two lengths, and `cross` of anything but two 3-vectors.

The mode is bound before everything else, which is the second time an ordering problem has come
out of `render { }` and the first time it was cheap: `SceneBuilder` walks the bound entries
twice, taking the `render` block on the first pass. Nothing else depends on order, so a scene
naming the mode at the bottom of the file means it for the camera at the top, and the cost is a
walk rather than a rule. The binders convert as they read, so the scene model still holds
degrees and nothing downstream knows the mode exists.

**What it cost, stated plainly.** The library reserves twenty-six names, and nothing shadows
here, so `floor`, `min`, `max`, `length`, `round`, `sign` and `cross` are no longer available
to a scene. **Three scenes in this repository bound `floor` as a material** and were renamed to
`ground`: `chess-full`, `chess-half` and `translucency`. That is the first time a language
change in this document has edited a scene file, and it is the honest price of the
no-shadowing rule, which is worth more than the words: an override that silently redefined
`floor` for a whole file would be far worse to find. `struct` is reserved too; no scene used it.
A second caveat belongs beside the determinism this project claims elsewhere. `sin` and its
neighbours go to the platform's maths library and are not promised to the last bit across
operating systems, so a scene computing geometry through one may differ by a float on another
machine. `random` and `perlin` are unaffected, and nothing under `scenes/manual/` uses either.

### The four the iteration finished on

**An array written as a child contributes its elements.** `union { shapes }` places all of
them. The question behind it was whether a field holding an array and a child holding one
should differ that much, and they should: a field has a name and a declared meaning, so
`points: [[0, 0], [1, 0]]` is one list and has to stay one; a child position means "a thing
that belongs here", and a list of those belongs here. It flattens all the way down, which costs
nothing to explain and covers a list of rows, and which loses nothing because an array was
never a valid child on its own. The one price is a diagnostic: `[1, 2, 3]` written as a child
now reports three times rather than once, and each message is right.

**Assignment to a part, without making anything mutable.** `a[0] = x` and `p.x = 3` exist now,
and the question they were parked on, whether passing a struct copies it or shares it, turned
out not to need answering. Assigning **rebuilds** the containers along the path and rebinds the
*root name*; nothing is mutated, so no other binding can observe the write, a field assignment
inside a function is invisible to the caller, and `let q = p;` neither copies nor shares because
there is nothing to copy and nothing to share. That is the answer this language already gives
for solids, where referencing a binding twice instantiates it twice, and it means arrays and
structs did not become the only values here with an identity that survives being passed around.
The cost is a copy of each container along the path, which is what an immutable value model buys
the rest of the evaluator: nothing else has to defend against aliasing. The left of an
assignment has to start with a name, since that is what the result is written back to;
`a[0]++` is refused by name, because widening `++` to a path would mean deciding how many times
the index between the brackets is evaluated.

**Modules, all three of them.** The boundary the mechanism never had:

- **`private`** in front of a `let`, a `function` or a `struct` keeps it inside the file that
  declared it. The marker is on what *stays*, which is the way round that leaves the common case
  unannotated: a file written to be imported is written for its bindings, and the helpers it
  does not want to publish are the few. It is consulted only at a file's outermost level, since
  no other frame is ever exported.
- **`import "materials.chroma" as materials`**, with `materials.gold` at the use site. A
  `ModuleValue` is an ordinary binding holding what the file exported, so two files may both
  define `gold`, and the dependency is legible where it is used rather than only at the top of
  the file. Calling through one needed the parser to read `(` after a `.`, decided by one token
  exactly as an identifier followed by `(` already was. It is a namespace and not a method call:
  the target must be a module, and nothing is bound to a first parameter.
- **The keyword changed to `import`**, which is what it has meant since iteration 8. `include`
  is reported and names the replacement, the way `fn` and the range loop are. One scene in the
  repository used it; the change is one word.

**A negative `scale` mirrors, and now something says so.** The case was known to be untested
rather than known to work: `ShapeCanonicalizer.Shareable` excludes a placement whose determinant
is negative and says why in as many words, and no scene exercised it. A chiral solid, a box
with a scoop bitten out of one corner and a coloured pip in the other half, was placed twice,
once at `scale: [-1, 1, 1]`, and rendered under symmetric lighting. **The two are exact mirror
images.** The pip and the scoop both swap sides, the concave cut surfaces are lit rather than
black, and the shadows mirror with them, which is the half that would have failed had the
inverse-transpose normal rule been wrong. `MirrorTests` pins what a test can reach without a
GPU: that the transform arrives with its handedness reversed, that two reflections compose back
to a rotation, and that a mirrored copy is not collapsed onto its original even with sharing
forced on.

### What was built, and what checks it

**Values and syntax.** `ArrayValue` replacing `VectorValue`, with `AsNumbers()` as the one place
"is this a vector" is asked; `StructTypeValue`, `StructValue` and `ModuleValue`; the
`Dot`/`Struct`/`Import`/`As`/`Private` tokens; `ArrayExpression`, `IndexExpression`,
`MemberExpression`, `StructStatement`, `PathAssignmentStatement` and `ImportStatement`;
`ParsePostfix` with its `IsIndexable` guard and its `(`- and `{`-after-`.` readings; `CallExpression`
and `ObjectExpression` gaining an optional module target.

**Evaluation.** `EvaluateArray`, `EvaluateIndex`, `EvaluateMember`, `ExecuteStruct`,
`BuildStruct`, `ExecutePathAssignment` and its `Rebuild`, `ExecuteImport`, `ResolveThroughModule`
and `AddChild`; a recursive `Equal`; `ResolveIndex` shared by reading an element and assigning to
one; `Scope.Exports` and `MarkPrivate` beside `IsBuiltin` and its flagged frame.

**Binding and beyond.** `BlockReader.Flatten` behind a `groupOf` argument, wired into `prism`,
`lathe` and `sphereSweep`; `BuiltinParameter`, `BuiltinArgument` and `BuiltinCall`, widening a
built-in from numbers-in-number-out; `RenderSettings.AnglesInRadians` with the two-pass bind; an
`angles` column on the dump's `Render` line.

**Verified.** 617 tests, 125 of them covering this iteration across `ArrayTests`,
`StructTests`, `MathTests`, `ImportTests` and `MirrorTests`: every element kind including nodes,
nesting, indexing and its refusals, `length`, equality in both directions for both kinds,
assignment through a path and its invisibility to every other binding, splicing at one level and
several, the two literal-adjacency cases the parser guard exists for, every struct diagnostic,
the whole scalar library, the four vector functions and what they refuse, a struct type built through its module, the radian mode
reaching a camera written above the block that sets it, both import forms, `private` against all
three declaration kinds, and a mirrored placement that is not shared. Two existing tests changed
their expected message, both correctly: a non-numeric element of a `center:` is now reported
where the vector is *read* rather than where it is written, and a bare array child reports per
element rather than once. `build-manual.ps1 -Verify` clean, and the mirror render looked at by
eye.

**Next.** Basic objects, in [suggestion.md](suggestion.md), now genuinely an alternative to a
thing that exists rather than to a thing that was proposed.

---

## Iteration 21: documentation rules, and the manual in the archive

**Deliverable.** [documentation-rules.md](documentation-rules.md): who each document is for, what
belongs in it, and when it is updated. Written once so it does not have to be restated, and
because two things had already gone wrong without it.

**What was wrong.** This document held four things at once: the record of each iteration, the
design rationale, the backlog, and the archive of what had been taken from the backlog. The
README advertised every design document with a paragraph of technical detail, which is dev
material in the public entry point. And the release archives carried a `README.md` whose three
images and twenty-nine links pointed into a `documents/` folder that was not in them, while the
illustrated manual, the one document someone who unzipped an archive actually needs, was not
shipped at all.

**What was built.**

1. [documentation-rules.md](documentation-rules.md): two audiences never mixed, one dev document
   per subject, what every entry of the language reference has to state, where illustrations live
   and that they must ship, the end-of-iteration cadence, and the house style.
2. [suggestion.md](suggestion.md): the backlog, moved out of here whole. Thirty-two entries, by
   theme. An entry leaves it when it is scheduled, not when it is finished.
3. [current_version.md](current_version.md): what the next delivery contains, kept current while
   the work happens.
4. `tools/publish-release.ps1`: the public documents and their images now go into every archive.
   Relative links to anything the archive does not carry are rewritten to the same file on GitHub
   at the release tag, and the build then asserts that every link a shipped document kept
   relative resolves inside the folder it just built. `RUNNING.txt` points at the manual in the
   folder rather than at a URL.
5. The README's documentation section, cut from paragraphs of technical detail to two lists of
   one line each: to write a scene, and to change the renderer.

**Verified.** `publish-release.ps1 -Runtime win-x64 -NoArchive` builds an archive whose README
has no relative link left pointing outside it, with 38 images beside the three documents; the new
assertion was checked in both directions, by running it against a folder with the images removed.
`build-manual.ps1 -Verify` clean. The thirty-two backlog entries were counted before and after
the move.

**Next.** Geometry and primitives, the four entries now in
[current_version.md](current_version.md), starting with the ones that reuse iteration 7's
flattening.

## Iteration 22: the geometry the existing primitives were missing

**Deliverable.** Five pieces of geometry the four list-shaped primitives could not express, plus
`quadric`. Four of the five were blocked on something that had already been removed, which is
what made them one iteration rather than five.

**What was built.**

1. **Several contours per `prism` and `lathe`.** The span path needed *nothing*. It already
   collects the ray's crossings of every wall, sorts them and pairs them consecutively, and
   pairing sorted crossings is the even-odd rule, so a contour drawn inside another comes out as
   a hole with nothing downstream of the binder aware of it. The cost was three small things:
   closing each contour back to its own first point rather than the solid's, a second level of
   array nesting in `BlockReader.ComponentGroups`, and a header texel in the shape buffer.
2. **The header texel, which is the structural change.** `paramA` now points at
   `(contourCount, smoothFlag, 0, 0)` followed by one `(start, count)` per contour, then the
   edges. `paramB` is the total edge count and is always positive. That retires the trick where
   the smooth flag rode in the *sign* of the segment count, which was the only slot left when the
   primitive record had two and could carry exactly one bit. `insideContour` is unchanged and is
   still called over every edge, because even-odd across all contours is precisely the right
   answer; `contourNormal` is the one function that had to learn where the seams are, since
   blending a joint's normal with `e ± 1` modulo the whole list would pair the first edge of one
   contour with the last edge of another.
3. **Bézier outlines for `prism`**, which after the header was a binder change and no GLSL at
   all: the flag lives in the header now, and `contourNormal` already read it.
4. **Curved paths for `sphereSweep`.** A path is not a contour, on every point that matters: it
   does not close, its very first control point is a real point of the result rather than the one
   the closing edge returns to, and it must not drop a repeated last point because repeating the
   first sphere is how a loop is closed. So `ReadBezierPath` is its own reader rather than
   `ReadBezier` at a different arity. The radius is the fourth component of the same cubic, and
   checking the *control* radii is the whole check, because a cubic stays inside the convex hull
   of its control points. `steps` defaults to 4 where an outline's defaults to 8: each step is a
   whole `roundConeSpan`, not a line segment.
5. **`blobCylinder`.** The field falls off with the distance to a segment, which is piecewise in
   three regions, and the piece in force changes where the foot of the perpendicular passes an
   end. In every region the squared distance is still *quadratic* in the ray parameter, so the
   field is still a quartic and `solveQuartic` never learns a capsule happened. What grows is the
   breakpoint count, four per capsule against two per sphere, and `gBreak` is now sized
   `2·spheres + 4·cylinders` where it was one number.
6. **`quadric`**, beside the sphere, cylinder and cone rather than subsuming them. Those three
   come with a slab, a known bound and a solve of a few lines, and re-expressing them here would
   cost every scene instructions to buy nothing. Two spans, not one: with a negative leading
   coefficient the ray is inside at both ends and outside in the middle, which is `coneSpan`'s
   downward-opening case with no slab to throw the mirror nappe away. Its box is
   `Aabb.Unbounded`, as `plane`'s is, and the answer to that is the language's own —
   `intersection { quadric box }` is both the clipping and the bounds, which is what POV-Ray's
   `bounded_by` is for.

**Two decisions worth recording.**

- **Every blob component is stored as a capsule**, a spherical one with both ends at the same
  point. Clamping the foot onto a segment of no length returns that point, so the shading
  gradient needed no discriminator at all: one closest-point expression covers both kinds. The
  *span* code still emits two loops rather than one with a runtime test, so a blob of spheres
  alone emits byte-identical GLSL to what it emitted before cylinders existed.
- **`--sdf` refuses a `quadric`.** The blob's `f / |grad f|` is kept there despite not being a
  distance because a blob is bounded, so an overshoot lands somewhere another test catches. A
  quadric is neither bounded nor as well behaved, and a scene that renders as noise with no
  diagnostic is worse than one that will not render.

**What it cost, and did not.** No change to the tape, the CSG operators, the cost model or the
span budget of anything that existed. The `MaxContourPoints` limit of 64 became a total across
contours rather than a per-contour cap, and `MaxSweepSpheres` is now applied after flattening.
`PrimitiveKind` gained a tenth value, and with it a test that reads the `KIND_` constants out of
`raytrace.glsl` and asserts they match the enum — the two files had each carried a comment saying
nothing checked that they agreed, and now something does.

**Verified.** 638 tests. Four renders on a 4070 SUPER for the failure modes no CPU test can see:
a pierced prism and a hollow lathe for the buffer offsets and the per-contour normal wrap, a
tripod and a cylinder-plus-sphere for the region split and the closest-point gradient, and a
hyperboloid of two sheets for the two-span branch. Three new manual plates.

**Next.** Iteration 23, rounding error as a subject rather than a constant, which 24 and 25 both
land on.

---

## Iteration 23: rounding error, as a subject rather than a constant

**Deliverable.** No tolerance in the renderer is a number anybody picked. `EPS` at 1e-4 and
`SHADOW_BIAS` at 1e-3 are both gone, and the derivation that replaced them is written up in
[csg-raytracing.md](csg-raytracing.md#rounding-error). PBRT chapter 6.8 is the source.

**The fault, restated.** The hit point is reconstructed as `o + t*d`, so its rounding grows with
`t` and with how far `o` sits from the world origin. The comment beside `SHADOW_BIAS` already
said so; what it did not say is that no constant can therefore be right at both ends of a scene.
Sized for the near field it stipples the far field with acne, sized for the far field it detaches
shadows near the camera and lets a thin solid vanish. Three symptoms, one fault.

**The decision the whole design rests on.** A `Span` is three words, and taking it from four was
the largest single speed-up in this renderer's history, so no per-surface quantity may ride in
one. That splits the problem in two, and the split is what made the rigorous version affordable:

- **Span bookkeeping** is every comparison on `t`, emitted **per leaf**, which is the code that
  meets the driver's instruction ceiling. It gets `tTolerance(t)`, relative and cheap, knowing
  nothing about which primitive produced the span.
- **Surface placement** is where a spawned ray starts. It gets the real bound, and it is
  evaluated **once per shaded vertex** in the hand-written shading half, which is compiled once
  whatever the scene holds. This is the same route the *normal* already takes, for the same
  reason and through the same function.

**What was built.**

1. **`tTolerance(t)` = `gamma(5) * (|t| + gTScale)`**, at the sliver guard, the union's
   coalescing test, both of `resolve`'s ends, `occludes` and `boundHit`. `gTScale` is the ray
   origin's magnitude expressed in `t`, and it is the half a purely relative tolerance misses: at
   `t` near zero what the point carries is the rounding of the *origin*. It is a global set at
   the top of `traceScene`, under the rule that already makes `gRoots`, `gCross` and `gDelta`
   globals. The driver inlines every call and allocates per variable, so a parameter threaded
   down to the operators would be storage at every call site of the scene walk.
2. **The bound on a surface is measured rather than propagated.** `primitiveNormal` returns
   `|F(p)| / |grad F(p)|` beside the normal, the first-order distance to the level set, which is
   the residual of the solver, the cancellation, the reconstruction and the transform *at once*.
   Every branch already computed the gradient; only the field value beside it is new arithmetic.
   Converting it out of the primitive's space costs one divide, by the length `hitNormal` already
   computes on its way to normalising the transformed normal, so a scaled instance is right for
   free where an absolute tolerance gave a solid a thousand times smaller than the scene the
   same number as the scene.
3. **`offsetOrigin`**, PBRT's `OffsetRayOrigin`, at all three sites that spawn a ray. The bound
   projected onto the normal, signed by the direction actually taken, then each component nudged
   one ulp further out through `floatBitsToInt`. That last step is what an offset expressed as a
   length can never fix by growing: below one ulp of the coordinate, no addition survives at all,
   which is exactly the regime a far-away scene puts every offset in. The sign used to be written
   out by hand at two of the sites, and getting it wrong renders glass perfectly black.
4. **Three tolerances deleted rather than replaced.** The cylinder, the cone and the prism each
   decided "cap or side" by testing `p.y` against `EPS`. They now take whichever surface the
   point is nearest, which is the question that test was approximating and needs no tolerance at
   all to ask.

**Why the quartic did not get a forward bound.** Full propagation through Cardano plus two
guarded Newton steps was the plan and is not what shipped. The closed form's error is dominated
by a cancellation whose size is not known in advance, so a bound honest enough to be conservative
would be uselessly wide. The residual form above is the rigorous counterpart at a root, it is
exact to first order, and it correctly blows up near a double root, which is where the root
genuinely is ill conditioned.

**Not built, and deliberately.** A per-leaf transform-error constant baked by the emitter was
planned and dropped. The world-space origin term already covers the cases it would have and does
so more conservatively, and it would have cost an instruction in every leaf, which is the
resource this shader runs out of after registers.

**What is left, and both are now relative rather than absolute.** The contour sign probe, at
`max(2 * deviation, 1e-3 * nearest edge length)`, which sizes a step for a *boolean* where being
wrong turns a normal over instead of moving a surface; ten absolute `EPS` was a step a contour
scaled down by a thousand crossed straight over. And the shadow walk's advance at
`4 * tTolerance(t)`, the one spawned ray with no normal available. Fetching one would mean a
`hitNormal` per boundary inside the walk to place a ray about to ignore the surface anyway.

**Verified.** 691 tests, `RoundingTests` new over the seam that a change here would break
silently: no scene emits an absolute tolerance, every span comparison is sized from the `t` it
compares, `traceScene` sets the ray scale before anything reads it, and all three spawn sites go
through `offsetOrigin`. That seam is worth holding precisely because an absolute tolerance
produces a correct picture for every scene near the origin, which is every scene in this
repository.

Two renders on a 4070 SUPER, which is what no CPU test can answer. `scenes/shapes.chroma` moved
100,000 units from the world origin comes back **identical to the same scene at the origin**;
before this iteration it came back with acne over every solid, concentric rings across the blob,
and the bored prism's lit face black with its bore lost. At 4,000 units the old code was still
fine, which is the useful half of the measurement: the failure is not gradual, it arrives when
the scene's own ulp passes the constant.

**Byte identity does not hold and is the wrong question here.** Tolerances changed, so
comparisons at silhouettes changed, so pixels changed. Any check phrased against a pre-iteration
render is answering something else.

**Next.** Iteration 24, meshes, whose watertight ray-triangle intersection is the part of PBRT
6.8 this one did not need.

---

## Iteration 24: meshes

**Deliverable.** `mesh { file: "assets/teapot.obj" }` is a solid like any other: a CSG operand,
instanced like any other shape, transformed like any other shape. `.obj` and `.stl` are read,
both encodings of the second. `scenes/meshes.chroma` renders the Utah teapot and the Stanford
bunny, and subtracts a sphere from the bunny to show that the result is a solid rather than a
surface. Written up in [meshes.md](meshes.md), traced in
[csg-raytracing.md](csg-raytracing.md#mesh--parity-in-three-dimensions).

**The decoder was the small half.** OBJ is lines of numbers and STL is a fixed record repeated;
neither is where the work went. The work went into one sentence from iteration 6, which refused
POV-Ray's `open` cones and has been true of every shape since: a CSG operand needs a well-defined
inside. Every other primitive satisfies it by construction. A mesh is described by a file, and a
file can say anything, so this is the first primitive that can be refused for what it *contains*.

**Three failures, one table.** Take every triangle's three edges as directed pairs in winding
order. A closed, manifold, consistently oriented surface has each directed edge exactly once and
its reverse exactly once. A missing reverse is a hole; a directed edge twice is two neighbours
disagreeing about which side is out; more than two triangles on an edge is non-manifold. One
dictionary, one pass, and each diagnostic names a count and a position.

**Only the hole is repaired, and only when asked.** `close: true` fills each with a fan of
triangles round its own centre, which is what lets the teapot, published open at its rim, be used
at all. The other two are refused whatever `close` says: filling a hole in a mesh whose triangles
disagree about which side is out gives a definite inside that is definitely wrong. The fan is not
a proof either, so the table is rebuilt and read again after capping.

**Five things this iteration settled.**

1. **Spans, not the nearest hit.** The traversal cannot stop at the first triangle and cannot use
   the front-to-back early-out that makes a hierarchy fast in an ordinary ray tracer. It collects
   every crossing, sorts and pairs, and pairing sorted crossings *is* the even-odd rule the prism
   already runs one dimension down. `boundHit` could not be reused for the node test because it
   takes a `limit`; `meshBoxCross` is the same slabs with no limit and nothing rejected behind
   the eye.
2. **The tie-break is the lathe's, in three dimensions.** PBRT 6.8's shear and permutation are
   functions of the ray alone, so two triangles sharing an edge get exact negations of each
   other's edge functions. Where PBRT reaches for double precision on an exact zero, GLSL 3.30
   has none, so an antisymmetric rule on the directed edge settles it instead. Iteration 23 came
   first for this.
3. **The cost model took it exactly as iteration 15 predicted.** The traversal loop takes its
   bound from the shape buffer rather than a literal, so the driver compiles one tree step
   instead of one per node. 112,402 triangles is 105 statements; `scenes/meshes.chroma` with four
   meshes in it is 343, one percent of the budget. What a mesh spends is memory.
4. **`InstanceBvh` became `Bvh`.** It was always a hierarchy over a list of boxes that knew
   nothing else about them, so the triangle tree is the same call with triangle boxes and the
   same two texels on the GPU. The rename is the whole of the change.
5. **A collision that would have been silent, and was not in the plan.** Two roots are decided to
   be one shape by comparing the GLSL they emit. A mesh's geometry is not in its GLSL: the body
   carries one offset into a buffer, and inside the probe that does the comparison every buffer
   starts empty, so every mesh emits offset zero. A teapot and a bunny would have compared equal
   and the second drawn as the first. Fixed with a content hash carried on `LeafPlan` and written
   into the body as a comment, which the cost model does not count.

**The first node to take a boolean field**, which had been sitting in the suggestions since
iteration 19 waiting for a user. `close` and `smooth` are both booleans read straight out of the
block, and `BlockReader` grew `Flag` and `Text` for them: the second is the first field in the
language whose value is neither a number nor a name, because a path has to be one.

**Vertex normals, and not UVs.** `smooth: true` interpolates normals across each triangle, which
is iteration 7's lesson repeated: the bunny's triangles are smaller than a pixel, so the faceted
version reads as noise rather than as facets. Where they come from is not the obvious answer —
OBJ indexes normals separately from positions, and the natural reading of that tears the mesh
apart topologically, so the topology is the positions and a position's normal is the average of
every normal quoted for it. UVs are parsed and discarded: nothing reads them until textures land.

**Verified.** `dotnet test` clean at 715, with `MeshTests` new over every seam: the OBJ face
forms including negative indices and polygon fans, both STL encodings, welding an STL cube back
to eight vertices, each of the three refusals, `close` repairing the one that is repairable, the
shape-buffer offsets, the escape indices, one upload for two placements of one model, and the
cost being equal for two meshes differing fourfold in size. `scenes/meshes.chroma` renders.

**What it cost that was not foreseen.** The test suite went from 3 seconds to 2 minutes 11: the
bunny is loaded, welded and hierarchised once per `mesh` node and again per probe, and the
repository-scene theories compile that scene several times. Nothing is wrong with the picture;
the loader simply has no memory. Recorded in [suggestion.md](suggestion.md).

**Next.** Iteration 25, a height map, whose grid march has the same cell-boundary rounding
problem and whose data has the same nowhere-to-go that this one solved with the shape buffer.

---

## Iteration 25: a height map

**Deliverable.** `heightField { height: terrain, resolution: 256 }` is a solid like any other: a
CSG operand, instanced like any other shape, transformed like any other shape. The samples come
from the scene, either as a function called once per sample or as a grid of numbers written out.
`smooth: true` interpolates normals across a cell, as a mesh's does. `scenes/terrain.chroma`
renders an island of five-octave `perlin` with a crater in it and the sea around it, both built
from one field. Written up in [height-fields.md](height-fields.md), traced in
[csg-raytracing.md](csg-raytracing.md#height-field-a-march-over-known-data).

**The data was the interesting half, and it is not an image.** An image file would need the first
image *decoder* in this solution, `PngWriter` being hand-rolled and write-only, and it would give
up the property every byte-identity check here rests on: a scene reproducible from the file that
describes it. `perlin` was already built, already deterministic on every machine and already
evaluated at bind time, so the scene computes its own terrain and nothing is read from disk.

**Calling a scene function from a binder is new, and it is five lines plus a paragraph.**
`Evaluator.EvaluateCall` resolves a name and evaluates argument expressions before running a body;
a binder holds the callee and the values already, so the tail became `Evaluator.Invoke` and
`BindingContext` carries the evaluator. Re-entering after `Execute` has returned is safe because
the return flag, the return value and the call depth are all clear by then and `Invoke` leaves
them as it found them, which is written down in the XML doc rather than left to be rediscovered.
A built-in routes through the same call, so `height: perlin` is a landscape in one line.

**Coordinates, not indices.** The function is called with the x and z of each sample. That single
choice is what makes `resolution` mean *how finely* rather than *what shape*, so raising it
refines the same landscape rather than describing an unrelated one, and it is what makes the field
better than the nested loop a scene could already write.

**Five things this iteration settled.**

1. **The box is the walls and the floor.** Clip the ray to `[-1, 1]` in x and z and `base` to just
   over the tallest sample in y, and inside that interval the solid is exactly `y ≤ H(x, z)`. The
   four walls and the floor are then the box's own faces and never have to be intersected: one
   point test at the entry says whether the ray starts underneath, and that boolean is the whole
   of the bottom half of the solid. It is the prism's slab test one dimension up.
2. **Grid space is a correctness decision.** Scaling the footprint to `[0, cells]` leaves `t`
   untouched and makes a cell corner a small integer, exact in a float, so two cells sharing an
   edge compute its endpoints from identical bits. PBRT's shear is a function of the ray alone, so
   iteration 23's watertightness argument then has nothing left to assume. `meshHit` split into
   `triangleCross` plus three fetches, and there is one watertight test and one tie-break in the
   file rather than two.
3. **The cost model took it again.** The march takes its bound from the shape buffer rather than
   from a literal, so the driver compiles one step instead of one per cell: about 104 statements
   whatever the grid holds, and `scenes/terrain.chroma` is 329 for the whole scene, one percent of
   the budget. What grows is memory, and this time also **load**: a million samples is a million
   calls of a scene function through a tree-walking interpreter, which is nine seconds.
   `GpuLayout.MaxHeightFieldResolution` at 1,024 bounds that as much as it bounds the 4.2 MB.
4. **Smoothing stores nothing, which a mesh could not do.** A mesh's vertex normals come from a
   file and have to be uploaded. A height is a function of two coordinates, so the normal at a
   sample is a central difference over its four neighbours computed at the hit, which saves an
   array larger than the heights and makes `smooth` change one shader function and nothing about
   the packing.
5. **`MeshOffsets` became `BlockOffsets`, and `MeshFile.Signature` became `ContentSignature`.**
   Both were already about a content rather than about a mesh, and a height field has exactly the
   mesh's probe trap: its body carries one buffer offset, every buffer starts empty inside a
   probe, and two different terrains would have compared equal. Same fix, now written once.

**The fault the entry could not have seen.** The default floor was the lowest sample, which is
what makes a terrain a solid without the scene saying where the ground is. Level with the minimum,
though, the solid has zero thickness wherever the terrain reaches its own floor, and any function
that clamps reaches it over an **area**. A ray entering there is neither inside nor outside, the
parity turns on the last bit of `origin + t * direction`, and the camera ray and the shadow ray
leaving the same point disagree. It rendered as a band of the surface shadowing itself, which
moved with the light and did not shrink with resolution, and so read as a shading bug for as long
as it took to rule out the normals, the offset, the span budget and the geometry one at a time.
The floor now sits a ten-thousandth of the terrain's height below the lowest sample, and the lid a
ten-thousandth above the tallest, which is free because the lid is not a surface of the solid at
all. Both are in [height-fields.md](height-fields.md).

**A silent fall-through, found while adding a case to it.** `LeafEmitter.Body`'s `switch` ended in
`default: SphereSweep(...)`, so a kind added to the enum and forgotten there compiled, rendered,
and was quietly the wrong solid. It now throws.

**Verified.** `dotnet test` clean at 741, with `HeightFieldTests` over every seam: both source
forms, the same terrain agreeing at two resolutions, a built-in as the function, each refusal, the
floor's default, the block laid out as the shader reads it, four samples to a texel, one upload
for two placements, a smooth field and a faceted one being two blocks, the cost being equal for
two grids differing a hundredfold in texels, two different fields told apart, and a `difference`
compiling at the right width. `FunctionTests` grew one over `Evaluator.Invoke` directly.
`scenes/terrain.chroma` and `scenes/manual/primitive-heightfield.chroma` render.

**Next.** Nothing after this in the 0.22.0 theme. What the iteration proposed and did not build,
an image decoder owed three times over and a min-max pyramid for grazing rays, is in
[suggestion.md](suggestion.md).

---

## What is still open

Moved to [suggestion.md](suggestion.md), which is the single list of what this project has
proposed and not built. An entry leaves that list in the iteration that takes it, and what it
settled is recorded in that iteration's section above.

## Already taken from the suggestions

Kept because the reasoning that went into them is the reasoning that will go into the next ones.
Five more entries left [suggestion.md](suggestion.md) for iterations 8 to 13: language control
flow, register pressure, adaptive sampling, performance, and the naming question. Adaptive
sampling is the one that went into an iteration and came back out of it unbuilt, which is why it
is still open, in that document's compiler section, rather than here.

**~~Noticing a new release~~, built as `src/Chroma/UpdateCheck.cs`.** All six points held and
five of them were built as written: detect and never update, off the render thread with every
failure silent, versions compared as numbers rather than as strings, `Chroma.SceneDump` left out
of it entirely, and one line on the console and one at the foot of the overlay with the answer
cached so that ten scenes in an afternoon cost one request.

Two things the entry did not settle, and they turned out to be the same thing. **"One check per
run" and "a line on the console" are in tension**, because the console line has to be first to be
read at all and the scene line prints a few hundred milliseconds in, which no request can beat.
What resolves it is that the cache the entry asked for is not an optimisation: it is the source
the console line reads from, and the request behind it is for the *next* run. Say that the other
way round and the feature acquires a blocking startup that no amount of care afterwards removes.

**The link is the second.** A line saying a newer version exists is worth much less than one
saying where it is, and a URL in an ImGui overlay is not clickable by default, so a hyperlink was
drawn out of coloured text, the item rectangle and a draw list. That put a network-supplied string
into `Process.Start` with `UseShellExecute`, which launches whatever is registered for the scheme
it carries, so the URL is now validated to be `https` on `github.com` before it is stored, let
alone opened. The entry did not predict that a detect-only feature would need that check.

Point 2 was built one half short on purpose: the flag is there, the persisted off-switch is not.
The cache file holds the answer and its date and nothing else, which keeps the one piece of state
this feature owns a piece of state it can regenerate.

**~~Macros~~ — built, as `function`.** Split out of iteration 8 to keep it bounded, and taken
on its own afterwards. The prediction above held exactly: it is a callable value plus argument
binding and no new machinery, because `Scope` was already a chain and a loop iteration
already bound a name into a fresh frame. A `function` declaration is a `let` that takes
arguments, its body is a statement list ending in `return`, and the closure is the scope of
the declaration rather than of the call — which is `include`'s asymmetry one level down, and
what makes a fragment of `function` declarations the parameterised fragment `include`
deliberately was not.

Two things it cost that the entry did not predict. **Recursion had to be budgeted**: a body
can see the name being declared, so a function can call itself, and iteration 8's argument —
that the loader must never fail by disappearing — applies again. Depth is capped at 64 and
calls at 100 000 per load, and both limits are needed: depth alone does not bound a recursion
that branches. (**The call budget was removed in iteration 18** with iteration 8's. Depth
stayed, on the narrower argument that a stack overflow is the one failure that cannot be
reported *or* interrupted. A recursion that branches now runs.) And **`object` came with
it**, because functions made the gap obvious: a
binding referenced on its own takes no modifiers, so placing one meant a `union` of one
operand. `object` is that union under an honest name, and it costs nothing — a single operand
emits no operator instruction.

**~~A syntax the language could settle on~~ — done, and it is JavaScript's.** The decision
table of iteration 0 called the dialect provisional and promised a revision. Iteration 8 and
the functions above were both additive, so the revision itself was still owed, and this is
it: `function name(a) { return … }` for declarations, `for (let i = 0; i < n; i++)` for
loops, `condition ? a : b` where a value is chosen, mandatory braces around every body, and
`%` beside the arithmetic that was already there. `fn`, the range loop and `if` in expression
position are gone.

Three consequences worth recording, because none of them is syntax:

1. **Bindings became mutable.** A C-style loop is a counter that changes. Making only the
   counter mutable would have been two rules where JavaScript has one, so `let` carries it —
   and assignment still never *declares*, which keeps a misspelling an error.
2. **The loop stopped being bounded by construction.** `for (;;)` parses. The iteration
   budget was a guard against an absurd count and is now the only thing that ends such a
   loop, which retires the `while` question in the other direction: `for (; c; )` is one.
3. **Every scene migrated to a byte-identical dump.** That is the measurable form of "the
   notation changed and the meaning did not", and it is the same check iteration 8 used —
   though here it had to pass with the files rewritten rather than untouched.

