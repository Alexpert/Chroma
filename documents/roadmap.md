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
| 9 | Measured against the state of the art | planned |
| 10 | Participating media: scattering and fog | planned |
| 11 | Speed, at equal image | planned |
| 12 | The illustrated manual | planned |

The whole path from a scene file to pixels exists. Nothing of the original boilerplate
remains: the cube, its shaders and the matrix pipeline are gone, replaced by a fullscreen
quad and a ray tracing shader driven entirely by buffers.

**Why the remaining four sit in that order.** Iteration 9 comes first of them because media
and speed each have several defensible algorithm families, and choosing between them by
reading once is cheaper than choosing twice. Media precedes speed so that the optimisation
work targets the finished renderer rather than a snapshot of it. The manual is last because it
documents 8's syntax and 10's nodes.

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
| Scene → GPU | Data buffer + generic GLSL interpreter, not code generation | One stable, debuggable shader; changing scene costs an upload, not a recompile |
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
script yet. Building that path is iteration 12's second item, and it is the point at which
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

**Research first**, to the standard of iterations 4 and 5: the radiative transfer equation, free
flight sampling, phase functions, and transmittance estimators, complete enough to implement
against. It extends `transparency.md` rather than starting a document, since absorption is
already there and is the same integral with a term missing.

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

---

## Iteration 12 — the illustrated manual

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

---

## Beyond — candidates, not commitments

Roughly in the order they would pay off. Five entries left this list for iterations 8 to 12 —
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

**Macros.** Split out of iteration 8 to keep it bounded, and now the largest thing the
language is missing: iteration 8's `include` is deliberately unparameterised, so a fragment
that wants an argument has nothing to be one. The frames it needs exist — `Scope` is a chain
and a loop iteration already binds a name into a fresh one — so a macro is a callable value
plus argument binding rather than new machinery. The preprocessor route would have made it
textual substitution with no scoping at all, which was the second reason that decision went
the way it did.

**Heterogeneous media.** Split out of iteration 10 for the same reason: a density field, whether
procedural noise or a 3D texture, plus delta or ratio tracking to sample free flight through it.
Nothing in iteration 10 needs to be built differently to make this reachable.

**The named limits.** [transparency.md](transparency.md#limits-of-this-implementation) lists
what the renderer cannot do — nested media, dispersion, subsurface scattering, shadow rays that
do not refract. None of them is scheduled, and none of them should be until iteration 9 has
priced them; the point of that iteration is to replace an ordering by intuition with one by
measurement.

**Surface detail.** Procedural patterns — POV-Ray's pigments and normals: checker, gradient,
noise — mapped through the primitive's *local* space, which the baked inverse matrix already
provides at no cost. Normal perturbation for bumps. Both are material-side and touch no
geometry.

**Workflow.** Hot-reload of the scene file on a `FileSystemWatcher` — the parse-to-upload
path is fast and stateless, so this is nearly free and changes how the tool feels to use.
Orbit camera on the mouse.

**Testing.** The front end is covered; the renderer is not, and cannot be by the same
means. A CPU reference implementation of the span algorithm, as another `ISolidVisitor`,
would fix that: the algorithm is already specified independently of GLSL, and having it in
C# turns "the picture looks wrong" into an assertable unit test. It is worth more now than
when it was written, since iteration 9 needs a trusted reference and a second renderer is a
much heavier way to obtain one.
