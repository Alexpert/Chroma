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
| 3 | CSG operators | not started |

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

**Done when** swapping `difference` for `intersection` in the scene file changes the shape
as expected, and the cavity's interior is lit rather than black.

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

**Shading.** Reflections, which need the shader's ray loop to iterate rather than shade
once; refraction, which also needs `merge` semantics to stop internal surfaces showing;
soft shadows from area lights; ambient occlusion.

**Image quality.** Supersampling, then adaptive sampling on edges. Cheap and very visible.

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
