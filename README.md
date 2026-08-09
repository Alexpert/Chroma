# ChromaTest

A GPU ray tracer for **CSG** — Constructive Solid Geometry. You describe a scene in a text
file, pass the file to the program, and it renders the solids by tracing rays against them
in a shader.

CSG builds shapes by combining simpler ones with boolean operators: a bolt is a cylinder
*union* a hex head *minus* a threaded groove. Rather than triangulating that, the renderer
intersects rays with the boolean expression directly, so the surfaces are exact at any
distance and there is no mesh anywhere in the pipeline.

```js
// scenes/csg.chroma — a box with a spherical bite taken out of it

camera { position: [0, 2, -5], lookAt: [0, 0, 0], fov: 45 }

pointLight { position: [2, 4, -3], color: [1, 1, 1] }

difference {
  box    { min: [-1, -1, -1], max: [1, 1, 1] }
  sphere { center: [0, 0, 0], radius: 1.3 }

  material: { color: [0.8, 0.2, 0.2], specular: 0.4 }
}
```

```sh
dotnet run --project src/ChromaTest -- scenes/csg.chroma
```

## Status

Early. Iteration 0 — design and documentation — is done; no renderer exists yet.

| Iteration | Deliverable | State |
| --- | --- | --- |
| 0 | Design and reference documentation | done |
| 1 | Scene parsing + hierarchy dump tool | not started |
| 2 | First render: camera, lights, sphere / box / cylinder | not started |
| 3 | CSG operators: union, intersection, difference | not started |

What is in the repository right now is the Silk.NET boilerplate the project started from: a
window drawing a normal-coloured cube. `dotnet run` still opens it. Iteration 1 restructures
the solution and iteration 2 replaces the rendering.

See [documents/roadmap.md](documents/roadmap.md) for the detail.

## How it works

The split between CPU and GPU is the design's centre of gravity, and it is what distinguishes
this from POV-Ray, the obvious point of comparison. POV-Ray parses and traces on the CPU.
Here the CPU parses and *compiles*, and the GPU traces.

```
.chroma file  ->  lex / parse / bind  ->  scene tree  ->  flatten to a tape  ->  upload
                                                                                    |
                        one fullscreen quad, fragment shader traces every pixel  <--+
```

Two decisions carry most of the design:

**Exact intervals, not distance fields.** A primitive does not answer "where is your nearest
surface" — it returns every *span* of the ray that lies inside it, and the operators merge
those span lists. This is the classic Roth formulation, and it is what makes `difference`
produce a genuinely correct cavity with correctly flipped normals, rather than the
approximation that `max(a, -b)` on signed distance fields gives.

**One generic shader, driven by data.** The scene tree is flattened into a post-order
instruction tape and uploaded as a texture buffer; the shader walks it with an explicit
stack, since GLSL has no recursion. Changing the scene re-uploads a buffer — it never
recompiles a shader. The shader stays a single readable file you can debug.

Both are written up in full in [documents/csg-raytracing.md](documents/csg-raytracing.md).

## Requirements

- .NET 8 SDK
- A GPU driver exposing OpenGL 3.3 Core

## Documentation

- [documents/scene-language.md](documents/scene-language.md) — the `.chroma` format:
  grammar, every node and field, and an appendix of the POV-Ray syntax it was measured
  against
- [documents/csg-raytracing.md](documents/csg-raytracing.md) — spans, the three merge
  operators, primitive intersection formulas, the GPU tape and buffer layout
- [documents/architecture.md](documents/architecture.md) — the three stages, the project
  split, and why the boundaries sit where they do
- [documents/implementation.md](documents/implementation.md) — per-file notes and a
  symptom-to-cause pitfalls table
- [documents/roadmap.md](documents/roadmap.md) — iterations and what comes after

The two reference documents are deliberately self-sufficient: implementing against them
should not require looking anything up online.

## Scope

This is a proof of concept. Correctness and replaceable boundaries come first; there is no
acceleration structure, no BVH, and no attempt at speed. The scene language covers only what
the renderer can draw and is expected to be **revised**, not merely extended, once loops and
macros are taken on.
