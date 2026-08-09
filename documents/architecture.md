# Architecture

## Purpose and scope

ChromaTest exists to make GLSL iteration cheap: open a window, draw one cube, and let the
shader files be the thing you change. Everything that is not needed for that is left out
on purpose — there is no scene graph, no material system, no asset pipeline, no camera
controller, and no animation. The intent is that a reader can hold the entire program in
their head in a few minutes and extend it in whatever direction they need.

## Why OpenGL 3.3 Core

The backend choice drives most of the code size.

| | OpenGL 3.3 Core | Vulkan |
| --- | --- | --- |
| Boilerplate to first triangle | ~200 lines | ~1500 lines |
| Shader format | GLSL text, compiled by the driver at startup | SPIR-V, compiled offline by `glslc` on every edit |
| Edit-to-pixel loop | edit file, re-run | edit file, run compiler, re-run |
| Explicit synchronisation | none (driver-managed) | queues, fences, semaphores, command buffers |

For a shader sandbox, the driver compiling GLSL directly from a text file is the feature
that matters — it removes an entire build step between the edit and the result. Version
3.3 Core is the lowest version that still has everything the modern pipeline needs (VAOs,
`layout(location = ...)` qualifiers, no fixed-function leftovers), and it is the ceiling
that keeps macOS viable should the project ever move there.

## Layers

```
Program.cs             entry point, window + GL lifecycle, per-frame orchestration
   │
   ├── Rendering/Shader.cs    GPU shader program: compile, link, bind, set uniforms
   └── Rendering/Cube.cs      GPU geometry: vertex/index buffers and the draw call
              │
              └── Shaders/*.vert, *.frag   the editable surface, read from disk at startup
```

Only three layers, and the dependency arrows all point one way. `Shader` and `Cube` know
about `GL` and nothing else — no static state, no knowledge of the window, no knowledge of
each other. `Program` is the only place that knows a frame exists.

The split is deliberately by *GPU resource kind* rather than by feature. A shader program
and a vertex array have genuinely different lifetimes and different failure modes, so they
are separate types; a "cube feature" that owned both would just be the whole program.

## Window lifecycle

`Silk.NET.Windowing` raises events in a fixed order; the program hooks four of them.

```
Window.Create(options)
        │
        ▼
     Load ──────── one shot, once the GL context exists
        │            create GL, create input context, compile shaders,
        │            upload geometry, enable depth test, set clear colour
        ▼
   ┌─ Update ───── not hooked: nothing in the scene changes over time
   │    │
   │    ▼
   │  Render ───── every frame: clear, bind program, upload matrices, draw
   │    │
   └────┘          loop until Close()
        │
        ▼
FramebufferResize  may fire at any point during the loop: reset viewport, rebuild projection
        │
        ▼
    Closing ────── dispose geometry, shader, input, GL — in reverse creation order
```

`Load` is the earliest point at which GL calls are legal: the context does not exist before
it. That constraint is why `_gl`, `_shader` and `_cube` are declared as fields initialised
to `null!` rather than constructed in `Main` — the alternative would be nullable checks in
the hot render path for a condition that cannot occur.

`Update` is intentionally not hooked. Silk.NET separates simulation from rendering so they
can tick at different rates; with a static scene there is nothing to simulate. Adding
rotation later means hooking `Update` and writing to a model-matrix field that `Render`
reads.

## Data flow of a frame

```
Model (fixed rotation)  ─┐
View (fixed camera)     ─┼─► Shader.SetUniform ──► GPU uniform storage
Projection (from size)  ─┘                              │
                                                        ▼
Cube VAO (positions + normals) ──► glDrawElements ──► vertex shader ──► rasteriser ──► fragment shader ──► framebuffer
```

The three matrices are separate uniforms rather than one pre-multiplied MVP. Pre-multiplying
on the CPU would save two matrix products per frame, which is irrelevant at this scale, and
would cost the shader author the ability to work in intermediate spaces — world position,
view-space normals, and camera distance all need the matrices apart. Since the point of the
project is shader authoring, the flexibility wins.

`Model` and `View` are `static readonly`: nothing animates. `Projection` is mutable because
it depends on the framebuffer aspect ratio, which changes when the window is resized.

## Coordinate and matrix conventions

This is the one place where the C# and GLSL sides disagree, so it is worth being explicit.

- `System.Numerics.Matrix4x4` stores its elements **row-major** and its factory methods
  (`CreateLookAt`, `CreatePerspectiveFieldOfView`, `CreateRotationY`) produce matrices for
  **row-vector** math: `v' = v * M`.
- GLSL reads uniform memory **column-major** and uses **column-vector** math: `v' = M * v`.

Uploading the raw bytes with `transpose: false` makes GLSL interpret the matrix as its own
transpose — and the transpose of a row-vector matrix is exactly the column-vector matrix
for the same transform. The two mismatches cancel. The visible consequence is that the C#
composition order and the GLSL composition order are mirrors of each other:

```
C#    (row-vector):    Model * View * Projection
GLSL  (column-vector): uProjection * uView * uModel * vec4(aPosition, 1.0)
```

The cube is a unit cube centred on the origin, in a right-handed space with +Y up and +Z
toward the viewer. The camera sits at `(0, 0, 3)` looking at the origin.

## GPU resource ownership

Every GL object has exactly one owning C# object, and that owner implements `IDisposable`.

| Owner | GL objects | Freed in |
| --- | --- | --- |
| `Shader` | program | `Shader.Dispose` |
| `Cube` | VAO, VBO, EBO | `Cube.Dispose` |
| `Program` | GL context, input context | `OnClosing` |

The intermediate vertex and fragment shader objects are a deliberate exception: they are
created, attached, linked, then detached and deleted inside the `Shader` constructor. Once
the program is linked, the stage objects are dead weight, and holding them would mean two
more handles to track for no benefit. They are also deleted on the failure paths, so a
compile or link error leaks nothing.

`OnClosing` disposes in reverse creation order. GL objects must be deleted while their
context is still current, so the `GL` instance is released last.

## Error handling posture

Shader files are located relative to the executable (`AppContext.BaseDirectory`), not the
working directory, so the program behaves the same launched from `dotnet run`, a
double-click, or a debugger. See the implementation notes for why this distinction matters.

The program fails fast and loudly rather than degrading. A shader that will not compile,
a shader that will not link, and a uniform name that does not resolve all throw with the
driver's own message attached. For a sandbox this is the right trade: a silent black window
is far more expensive to debug than a stack trace naming the file and line.

The one sharp edge worth knowing is that GLSL compilers strip uniforms the shader never
reads. Deleting `uModel` from a shader while `Program.cs` still calls
`SetUniform("uModel", ...)` produces an exception about a missing uniform, not a rendering
artefact. This is documented in the README because it will happen to anyone who edits the
shaders aggressively.

## Extension points

The design anticipates four changes without pre-building any of them:

- **Hot-reload** — `Shader` already isolates compilation behind a constructor, so reloading
  is "build a new `Shader`, and on success dispose the old one".
- **Animation** — hook `Update`, write to a model-matrix field.
- **Camera control** — the input context already exists in `OnLoad`; drive the view matrix
  from it.
- **More geometry** — `Cube` is a concrete class rather than an interface because there is
  exactly one mesh. A second mesh is the moment to extract a `Mesh` type taking a vertex
  array and a layout descriptor; doing it earlier would be abstraction without a second case
  to justify it.
