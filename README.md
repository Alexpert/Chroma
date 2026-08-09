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

camera { position: [0, 2, 5], lookAt: [0, 0, 0], fov: 45 }

// Radius softens the shadows without changing how bright the light is; intensity is large
// because light falls off with the square of the distance.
pointLight { position: [2, 4, 3], color: [1, 1, 1], intensity: 55, radius: 0.4 }

difference {
  box    { min: [-1, -1, -1], max: [1, 1, 1] }
  sphere { center: [0, 0, 0], radius: 1.3 }

  material: { color: [0.8, 0.2, 0.2], roughness: 0.4 }
}
```

```sh
dotnet run --project src/ChromaTest -- scenes/csg.chroma
```

## Status

It is a path tracer: light bounces, so a red wall tints the white floor beside it, metals
reflect their surroundings, and shadows have real penumbrae.

| Iteration | Deliverable | State |
| --- | --- | --- |
| 0 | Design and reference documentation | done |
| 1 | Scene parsing + hierarchy dump tool | done |
| 2 | First render: camera, lights, sphere / box / cylinder | done |
| 3 | CSG operators: union, intersection, difference | done |
| 4 | Correct lighting: bounces, PBR materials, soft shadows | done |
| 5 | Transparency, refraction, Fresnel, caustics | not started |

See [documents/roadmap.md](documents/roadmap.md) for what each iteration settled and why.

### Rendering

```sh
$ dotnet run --project src/ChromaTest -- scenes/cornell.chroma
cornell.chroma: 8 primitives, 5 materials, 1 lights
```

A 1280x720 window opens on the scene. `Escape` closes it. Everything in the file — camera
position, field of view, light colours, materials, transforms — takes effect on the next
run, with no rebuild and no shader recompilation.

**The image arrives noisy and cleans itself up.** One light path per pixel is traced per
frame and averaged into everything before it, so a still camera converges over a few seconds
rather than presenting a finished picture immediately. Resizing the window starts it over.

### Inspecting a scene

`ChromaTest.SceneDump` prints the hierarchy the parser understood. When a picture is wrong,
this is what tells you whether the file was read the way you meant.

```sh
$ dotnet run --project src/ChromaTest.SceneDump -- scenes/csg.chroma
Camera   position <0, 2, 6>  lookAt <0, 0, 0>  up <0, 1, 0>  fov 45

Lights
  +- PointLight        position <2, 4, 3>  color <1, 1, 1>  intensity 1
  `- DirectionalLight  direction <-0.57735, -0.57735, -0.57735>  color <0.25, 0.25, 0.35>  intensity 1

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

Mistakes in a scene file are collected and reported together, with a line and a column,
rather than one per run:

```sh
$ dotnet run --project src/ChromaTest.SceneDump -- scenes/diagnostics-demo.chroma
scenes/diagnostics-demo.chroma:8:5: error: 'radius' is already defined
scenes/diagnostics-demo.chroma:20:3: error: unknown field 'raduis' on 'sphere'
scenes/diagnostics-demo.chroma:24:8: error: field 'min' expects a vector of 3 components, found a vector of 2 components
scenes/diagnostics-demo.chroma:28:1: error: 'difference' needs at least 2 operands, found 1
4 errors; scene not loaded.
```

## How it works

The split between CPU and GPU is the design's centre of gravity, and it is what distinguishes
this from POV-Ray, the obvious point of comparison. POV-Ray parses and traces on the CPU.
Here the CPU parses and *compiles*, and the GPU traces.

```
.chroma file  ->  lex / parse / bind  ->  scene tree  ->  flatten to a tape  ->  upload
                                                                                    |
                        one fullscreen quad, fragment shader traces every pixel  <--+
```

Three decisions carry most of the design:

**Exact intervals, not distance fields.** A primitive does not answer "where is your nearest
surface" — it returns every *span* of the ray that lies inside it, and the operators merge
those span lists. This is the classic Roth formulation, and it is what makes `difference`
produce a genuinely correct cavity with correctly flipped normals, rather than the
approximation that `max(a, -b)` on signed distance fields gives.

**One generic shader, driven by data.** The scene tree is flattened into a post-order
instruction tape and uploaded as a texture buffer; the shader walks it with an explicit
stack, since GLSL has no recursion. Changing the scene re-uploads a buffer — it never
recompiles a shader. The shader stays a single readable file you can debug.

**Light propagates, so it has to be sampled.** Rather than a shading formula evaluated once,
each pixel traces a light path that bounces, and frames are averaged together. That is the
only way a surface can be lit by another surface — and it is why the image converges instead
of appearing finished.

The first two are written up in full in
[documents/csg-raytracing.md](documents/csg-raytracing.md), the third in
[documents/lighting.md](documents/lighting.md).

## Repository layout

| Path | Contents |
| --- | --- |
| `src/ChromaTest.Core` | the language and the scene model — no graphics dependency |
| `src/ChromaTest` | the Silk.NET application: window, upload, ray tracing shader |
| `src/ChromaTest.SceneDump` | the parser front end, made observable |
| `tests/ChromaTest.Core.Tests` | xUnit coverage of the whole front end |
| `scenes/` | sample `.chroma` files |
| `documents/` | design and reference documentation |

## Requirements

- .NET 8 SDK
- A GPU driver exposing OpenGL 3.3 Core

```sh
dotnet build ChromaTest.sln
dotnet test
```

## Documentation

- [documents/scene-language.md](documents/scene-language.md) — the `.chroma` format:
  grammar, every node and field, and an appendix of the POV-Ray syntax it was measured
  against
- [documents/csg-raytracing.md](documents/csg-raytracing.md) — spans, the three merge
  operators, primitive intersection formulas, the GPU tape and buffer layout
- [documents/lighting.md](documents/lighting.md) — the rendering equation, the
  metallic-roughness BRDF, importance sampling, light sampling, and convergence
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
