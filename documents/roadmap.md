# Roadmap

Where the project is going, in the order it is being built. Each iteration ends with
something runnable and demonstrable; nothing is built ahead of the iteration that needs it.

This is a **proof of concept**. Correctness and a clean, replaceable structure come first;
performance work is explicitly deferred and listed at the end.

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

The whole path from a scene file to pixels exists. Nothing of the original boilerplate
remains: the cube, its shaders and the matrix pipeline are gone, replaced by a fullscreen
quad and a ray tracing shader driven entirely by buffers.

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

**Deliverable.** `ChromaTest.SceneDump scenes/csg.chroma` parses a scene file and prints the
solid hierarchy. Nothing is rendered.

```
$ dotnet run --project src/ChromaTest.SceneDump -- scenes/csg.chroma
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

1. A four-project solution — `ChromaTest.Core`, `ChromaTest` (the existing app, moved to
   `src/`), `ChromaTest.SceneDump`, and `tests/ChromaTest.Core.Tests`.
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

**Deliverable.** `dotnet run --project src/ChromaTest -- scenes/primitives.chroma` opens the
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
   | Approximate or skip | Keep transparency and refraction, drop the caustic. Legitimate for a proof of concept, if stated. |

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

## Beyond — candidates, not commitments

Roughly in the order they would pay off.

**Language.** Loops and macros — the reason the current dialect is explicitly provisional.
POV-Ray solves this with a `#`-prefixed preprocessor layer running ahead of the parser;
whether to copy that or make the evaluator properly recursive is the open question, and it
is likely to reshape `Sdl/Binding/`. `include` for shared scene fragments comes with it.

**Geometry.** The curved spline types for `prism` and `lathe`, which are a CPU-side
tessellation into the segments both already understand; several contours per solid, which
needs a value model that can hold a list of lists; cylindrical blob components. Quadrics as a
general case would subsume the sphere, cylinder and cone. Meshes are the large one, and the
first thing here that would need an acceleration structure.

*(Iteration 6 took the six primitives that were listed here, and found that "one binder plus
one span function plus one normal function, the tape untouched" was right about the tape and
wrong about everything else — see above.)*

**Surface detail.** Procedural patterns — POV-Ray's pigments and normals: checker, gradient,
noise — mapped through the primitive's *local* space, which the baked inverse matrix already
provides at no cost. Normal perturbation for bumps. Both are material-side and touch no
geometry.

**Image quality.** Supersampling, then adaptive sampling on edges. Cheap and very visible —
and largely free once iteration 4 accumulates frames, since jittering the ray inside the
pixel is one line.

**Workflow.** Hot-reload of the scene file on a `FileSystemWatcher` — the parse-to-upload
path is fast and stateless, so this is nearly free and changes how the tool feels to use.
PNG export for non-interactive runs. Orbit camera on the mouse.

**Testing.** The front end is covered; the renderer is not, and cannot be by the same
means. A CPU reference implementation of the span algorithm, as another `ISolidVisitor`,
would fix that: the algorithm is already specified independently of GLSL, and having it in
C# turns "the picture looks wrong" into an assertable unit test.

**Performance — deliberately last.** Bounding volumes per subtree to skip whole branches,
early ray termination, and reducing register pressure in the span stack. None of it matters
until a scene is large enough to be slow, and all of it would obscure the algorithm while
it is still being made correct.

**Naming.** `ChromaTest` is inherited from the boilerplate and no longer describes anything.
Renaming is a one-time cost that grows with every file added.
