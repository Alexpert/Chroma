# Roadmap

Where the project is going, in the order it is being built. Each iteration ends with
something runnable and demonstrable; nothing is built ahead of the iteration that needs it.

Correctness and a clean, replaceable structure come first. Performance was deferred by policy
through the first seven iterations — optimising an algorithm still being made correct obscures
it — and is now scheduled, under a rule that stops it trading the image away for speed.

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
   [scene-language.md](scene-language.md#lexical-structure).
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
[scene-language.md](scene-language.md#coordinate-system) now spells out the consequence.

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
   [scene-language.md](scene-language.md#material), and the GPU material layout.
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
   [scene-language.md](scene-language.md#top-level-solids-are-unioned-but-not-merged) now
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
| Guard against a loop that runs for an hour? | **A budget of 100 000 iterations per load** | `for` cannot loop forever, but `for (i in 0..1000000000)` parses, and a loader that disappears reports nothing at all. The budget makes that reportable, and no scene worth writing comes near it — the lattice spends 125 |

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
   is the budget rather than the choice of `for` that closes the hole.

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
([scene-language.md](scene-language.md#limits-and-what-each-primitive-costs)); and a scene can
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

## Beyond — candidates, not commitments

Roughly in the order they would pay off. Five entries left this list for iterations 8 to 13 —
language control flow, register pressure, adaptive sampling, performance, and the naming
question, which is now settled rather than deferred.

**Geometry.** Bézier outlines for `prism` and curved paths for `sphereSweep`, both of which
reuse the flattening iteration 7 built; several contours per solid, which needs a value model
that can hold a list of lists; cylindrical blob components. Quadrics as a general case would
subsume the sphere, cylinder and cone. Meshes are the large one, and the first thing here that
would need an acceleration structure.

*(Iteration 6 took the six primitives that were listed here, and found that "one binder plus
one span function plus one normal function, the tape untouched" was right about the tape and
wrong about everything else — see above.)*

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
that branches. And **`object` came with it**, because functions made the gap obvious: a
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

**A `random` function.** Every generated scene so far is regular: a loop of a hundred posts
writes a hundred identical posts, and a scene that wants variation has to manufacture it out of
the loop counter with `%` and arithmetic, which is legible for a checkerboard and not for a
forest. `random` is what is missing, and it would be the language's **first built-in function**:
there is no `sin`, no `sqrt` and no `floor` today, so whatever adds it also settles how a
built-in is named, scoped and refused, for all of them.

Five questions come with it, and only the last is about geometry.

1. **Determinism is the feature, not a caveat.** Three things in this project rest on a scene
   loading to the same bytes twice: the manual's `-Check`, which compares 38 rendered images byte
   for byte; the dump comparisons that measured both language revisions as additive; and
   iteration 15's byte-identity sweeps across drivers and chunk counts. A value that varies per
   load retires all three at once. The seed therefore belongs to the scene, beside `maxBounces`
   and `exposure`, and the same file with the same seed has to produce the same image on any
   machine, which also rules out any generator with a platform-dependent step.

2. **A stream, or a hash.** `random()` returning the next value of a stream makes every result
   depend on the order the evaluator happens to walk the tree, so a change to that order silently
   redraws every scene that used it and no test would name the cause. `random(i)`, a pure
   function of its argument and the scene seed, has no order to depend on: the scene supplies
   what varies, usually the loop counter, and the value survives any refactor of `Evaluator`. It
   costs the scene one argument, and it is the form this project's constraints point at.

3. **One form, since the arithmetic already exists.** A number in `[0, 1)` composes with what the
   language has: `lo + random(i) * (hi - lo)` is the range. The integer case wants `floor`, which
   is exactly the "does the first built-in ship alone" question, and so is a vector-valued form.

4. **Naming, against the no-shadowing rule.** Nothing shadows here, deliberately. A built-in that
   is an ordinary binding in an outermost frame makes `function random(i)` in a scene an error
   rather than an override, which is the right behaviour and has to be reported as a collision
   with a built-in rather than with something the file cannot see.

5. **It interacts with instancing, and not gently.** Iteration 14 recovers shape identity by
   comparing generated GLSL, and iteration 15 partitions a scene by what its distinct shapes
   cost. A random *placement* changes neither: the placement is buffer data and the shape stays
   shared. A random *dimension* makes every copy a distinct shape, collapses the sharing and puts
   the scene on the cost model. `scenes/palisade.chroma` is that scene, written out by hand for
   this exact reason: two hundred posts of two hundred sizes. `random` would make it five lines,
   and it would make writing the scene that does not fit just as short.

**What it is not.** The shader has had a per-pixel, per-bounce PCG hash since iteration 4, and
this is not that one. `random` runs on the CPU at bind time, once per load; its results are baked
into the tape like any other number, and nothing about it reaches the shader.

**Heterogeneous media.** Split out of iteration 10 for the same reason: a density field, whether
procedural noise or a 3D texture, plus delta or ratio tracking to sample free flight through it.
Nothing in iteration 10 needs to be built differently to make this reachable.

**The named limits.** [transparency.md](transparency.md#limits-of-this-implementation) lists
what the renderer cannot do — nested media, dispersion, subsurface scattering, shadow rays that
do not refract. None of them is scheduled. Iteration 9 was to price them and is on standby, so
anything taken from this list before it runs is taken on intuition — which is a reason to say so
out loud, not a reason to avoid it.

**Surface detail.** Procedural patterns — POV-Ray's pigments and normals: checker, gradient,
noise — mapped through the primitive's *local* space, which the baked inverse matrix already
provides at no cost. Normal perturbation for bumps. Both are material-side and touch no
geometry.

**Workflow.** Hot-reload of the scene file on a `FileSystemWatcher` — the parse-to-upload
path is fast and stateless, so this is nearly free and changes how the tool feels to use.
Orbit camera on the mouse.

**Noticing a new release.** The archives are self-contained: no installer, no package manager and
no update channel, so a copy someone unzipped six months ago has no way of learning that a newer
one exists. Both halves of the comparison already exist. `Directory.Build.props` holds the one
version the assemblies report and `tools/publish-release.ps1` tags with, and
`api.github.com/repos/Alexpert/Chroma/releases/latest` answers with a `tag_name` for a single
unauthenticated GET. What has to be decided is everything around that request.

1. **Detect, do not update.** Downloading a build and replacing a running binary is a different
   feature, with signing, permissions and rollback inside it, and this project would open that
   discussion already owing macOS a signature it does not have (see the README). The deliverable
   is a line saying that a newer version exists and where it is.

2. **The first outbound connection is a property of the program, not a detail.** Today a scene
   goes in and pixels come out with nothing in between. Adding a check means saying so in the
   README, keeping it refusable by a flag and by a persisted setting, and leaving it out of the
   non-interactive path entirely: `--headless` and `--output` exist to produce the manual's
   byte-identical images and to run inside scripts, and neither wants a request to a third party
   or the latency and the failure mode that come with it.

3. **It must not be able to fail a render.** Off the render thread, short timeout, every failure
   silent: no network, no DNS, a proxy in the way, or GitHub's 403 once an address passes sixty
   unauthenticated requests in an hour. The window opens at the same moment whether the check
   answers, fails, or never returns at all.

4. **Compare versions, not strings.** The tag is `v0.13.0`, and `"v0.9.0" > "v0.13.0"` is true of
   strings and false of releases. Parse the three numbers and order those, and ask
   `/releases/latest` rather than the list, since that endpoint already excludes drafts and
   prereleases.

5. **`Chroma.SceneDump` stays out of it**, for a reason this document keeps meeting: the dump is
   compared byte for byte, by `build-manual.ps1 -Verify` and by every migration called additive
   here. A tool whose output can grow a line the day a release is published is a tool whose
   output is no longer a reference.

6. **Where it appears, and how often.** One check per run at most, with the answer and its date
   persisted so that opening ten scenes in an afternoon costs one request rather than ten. A line
   in the ImGui overlay and a line on the console, no modal, and nothing a reader has to dismiss
   twice.

**Testing.** The front end is covered; the renderer is not, and cannot be by the same
means. A CPU reference implementation of the span algorithm, as another `ISolidVisitor`,
would fix that: the algorithm is already specified independently of GLSL, and having it in
C# turns "the picture looks wrong" into an assertable unit test. It is worth more now than
when it was written, since iteration 9 will need a trusted reference whenever it runs, and a
second renderer is a much heavier way to obtain one.

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
