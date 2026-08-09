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
| 4 | Correct lighting: bounces, reflections, indirect | not started — research first |
| 5 | Transparency, refraction, Fresnel, caustics | not started — research first |

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

**Research first.** This is the first iteration whose reference document does not exist yet.
Write `documents/lighting.md` before writing code, to the standard iteration 0 set: complete
enough that implementing against it needs nothing from the web. Five questions have to be
settled there, and each one changes what gets built:

| Question | The trade |
| --- | --- |
| Whitted recursion-free loop, or Monte Carlo path tracing? | Whitted gives sharp reflections and hard shadows deterministically, in one pass, and no indirect diffuse — so no colour bleeding, so the deliverable above is unreachable. Path tracing gives all of it and costs an accumulation buffer plus noise. |
| Keep Blinn-Phong, or move to an energy-conserving BRDF? | Blinn-Phong is not energy-conserving; summed over bounces it either loses or invents light. Lambert + GGX is the standard answer and reshapes the material table **and the scene language** — `specular`/`shininess` become `roughness`/`metallic`. That is a breaking change to `.chroma` files and belongs in the same decision. |
| Delta lights only, or emissive solids? | `pointLight` and `directionalLight` are infinitesimal: a ray can never hit one, so they give hard shadows and no visible source. Area light means an `emission` field on a material and a solid that *is* a light — which is also the prerequisite for iteration 5's caustics. |
| Next-event estimation, or brute force? | Sampling the light at every bounce converges enormously faster; pure random bouncing needs an implausible number of samples to find a small light. NEE costs a sampling routine per light shape. |
| Fixed bounce depth, or Russian roulette? | Fixed depth is biased but trivial and reproducible; Russian roulette is unbiased and adds variance. At proof-of-concept scale, fixed depth is almost certainly right. |

**Work**, assuming the accumulating path tracer the questions above point at.

1. **Accumulation.** Render into a `GL_RGBA32F` texture through an FBO instead of straight
   to the screen, sum one new sample per frame, and resolve with a division by the frame
   count. Any change to the camera or the scene resets the counter. All of it is core in
   GL 3.3 — this does not force a version bump.
2. **A tone-mapped resolve pass.** Accumulated radiance is unbounded, and the current shader
   writes linear values straight out. A second fullscreen pass does tone mapping and gamma.
   Skipping it makes every result look wrong in a way that is easy to misread as a lighting
   bug.
3. **Per-pixel randomness.** A hash-based generator seeded by pixel and frame index. GLSL
   3.30 has no `uint` bit tricks problem here, but it has no state either: the seed must be
   threaded through the bounce loop by hand.
4. **The bounce loop.** `trace()` already returns a self-contained hit and is reusable as
   is. Around it: sample an outgoing direction from the BRDF, carry a throughput colour,
   accumulate emission. Still no recursion — the loop is bounded by a compile-time constant.
5. **Material and language changes** from question 2, including the migration of the sample
   scenes and the `material` table in
   [scene-language.md](scene-language.md#material).
6. **An emissive material and area-light sampling**, if question 3 goes that way. Note that
   an emissive solid needs no new geometry: it is an ordinary CSG solid whose material
   carries radiance.

**Watch for.** This is the iteration where performance stops being ignorable — every pixel
now traces the whole tape several times per frame. The response is *not* to start optimising
the span machinery, which would obscure an algorithm still being made correct; it is that
progressive accumulation converges while the camera is still, so an interactive frame stays
cheap and the image improves on its own.

**Done when** the ceiling of the test box takes the floor's colour with no light aimed at
it, the mirror sphere shows its neighbours, shadows have soft edges from the panel's size
alone, and a still camera visibly converges from noisy to clean.

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
darker than thin, two overlapping transparent solids under `merge` show no internal seam,
and — subject to the choice made in item 6 — a caustic appears under the sphere.

---

## Beyond — candidates, not commitments

Roughly in the order they would pay off.

**Language.** Loops and macros — the reason the current dialect is explicitly provisional.
POV-Ray solves this with a `#`-prefixed preprocessor layer running ahead of the parser;
whether to copy that or make the evaluator properly recursive is the open question, and it
is likely to reshape `Sdl/Binding/`. `include` for shared scene fragments comes with it.

**Geometry.** More primitives — `plane` (a half-space, and the natural ground), `cone`,
`torus`. Each is one binder plus one span function plus one normal function; the tape and
the operators are untouched. Quadrics as a general case would subsume several of them.

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
