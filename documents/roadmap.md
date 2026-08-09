# Roadmap

Where the project is going, in the order it is being built. Each iteration ends with
something runnable and demonstrable; nothing is built ahead of the iteration that needs it.

This is a **proof of concept**. Correctness and a clean, replaceable structure come first;
performance work is explicitly deferred and listed at the end.

## Status

| Iteration | Deliverable | State |
| --- | --- | --- |
| 0 | Documentation and design | done |
| 1 | Scene parsing + hierarchy dump tool | not started |
| 2 | First render: camera, lights, primitives | not started |
| 3 | CSG operators | not started |

The repository currently holds the boilerplate it started from: a Silk.NET window drawing a
normal-coloured cube. Iteration 1 restructures it and iteration 2 replaces the rendering.

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
Camera   position <0, 2, -5>  lookAt <0, 0, 0>  fov 45
Lights
  +- PointLight  position <2, 4, -3>  color <1, 1, 1>  intensity 1
Solids
  +- Difference                       material=red  translate <0, 0.5, 0>
     +- Box     min <-1, -1, -1>  max <1, 1, 1>
     +- Sphere  center <0, 0, 0>  radius 1.3
```

**Work.**

1. Split into a solution: `src/ChromaTest.Core/` (library), `src/ChromaTest/` (the existing
   app, moved), `src/ChromaTest.SceneDump/` (new console app). Add a `.gitignore`.
2. `Sdl/Source/` — `SourceText`, `SourceSpan`, `Diagnostic`, `DiagnosticBag`.
3. `Sdl/Lexing/Lexer.cs` — the token set from
   [scene-language.md](scene-language.md#lexical-structure).
4. `Sdl/Syntax/Parser.cs` — the EBNF, into an AST that knows **no** node names.
5. `Scene/` — `Solid`, `Sphere`, `Box`, `Cylinder`, `Union`, `Intersection`, `Difference`,
   `Transform`, `ISolidVisitor<T>`, plus `Camera`, `PointLight`, `DirectionalLight`,
   `Material`, `Scene`.
6. `Sdl/Binding/` — expression evaluator with `let` scope, `INodeBinder` +
   `NodeBinderRegistry`, one binder per node name, `SceneBuilder`.
7. `scenes/primitives.chroma` and `scenes/csg.chroma` — written now, so the parser has a
   real target before the renderer exists.
8. `SceneDump` — a `HierarchyPrinter : ISolidVisitor<...>`; non-zero exit on any error
   diagnostic.

**Done when** both sample scenes dump correctly, and a deliberately broken file reports
line/column diagnostics and exits non-zero.

---

## Iteration 2 — first render

**Deliverable.** `dotnet run --project src/ChromaTest -- scenes/primitives.chroma` opens the
window on a sphere, a box and a cylinder, lit by one point light and one directional light.

The span machinery is built here, in full, even though there is nothing to combine yet — a
single primitive is a one-span list, and that is the shape everything else plugs into.

**Work.**

1. `Compilation/CsgTapeBuilder.cs` — a visitor producing the post-order tape, the primitive
   table with baked inverse matrices, the material table, and the span/stack budget.
2. `src/ChromaTest/Rendering/FullscreenQuad.cs` replaces `Cube.cs`; `SceneBuffer.cs` creates
   and uploads the texture buffer objects.
3. `Shaders/raytrace.vert` — pass-through. `Shaders/raytrace.frag` — primary ray generation
   from the camera uniforms, tape decoding, spans for the three primitives, Lambert +
   Blinn-Phong shading. No CSG operator yet: the tape is leaves only and root objects are
   implicitly unioned.
4. `Program.cs` — scene file as a required command-line argument, parsed at `Load`; camera
   rebuilt on resize.
5. The missing `SetUniform` overloads in `Rendering/Shader.cs` (`float`, `int`, `Vector3`,
   arrays).

**Done when** moving the camera in the scene file changes the viewpoint with no rebuild, and
the three primitives shade correctly under both light types.

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

**Testing.** A CPU reference implementation of the span algorithm, as a third
`ISolidVisitor`. It costs little, since the algorithm is already specified independently of
GLSL, and it turns "the picture looks wrong" into an assertable unit test.

**Performance — deliberately last.** Bounding volumes per subtree to skip whole branches,
early ray termination, and reducing register pressure in the span stack. None of it matters
until a scene is large enough to be slow, and all of it would obscure the algorithm while
it is still being made correct.

**Naming.** `ChromaTest` is inherited from the boilerplate and no longer describes anything.
Renaming is a one-time cost that grows with every file added.
