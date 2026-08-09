# Architecture

## Purpose and scope

ChromaTest renders a scene described in a text file, by ray tracing CSG solids **on the
GPU**. The input is a `.chroma` file holding the camera, the lights and a tree of solids
built from primitives and boolean operators; the output is an image.

The distinction from POV-Ray, which is the obvious point of comparison, is where the work
happens. POV-Ray parses on the CPU and traces on the CPU. Here the CPU parses, validates
and *compiles* the scene into a compact buffer, and a fragment shader does the tracing. That
split is the reason the architecture looks the way it does: everything upstream of the GPU
buffer is ordinary, testable C# with no graphics dependency, and everything downstream is a
single generic shader that never needs to change when a scene does.

This is a proof of concept. Correctness and replaceable boundaries are the goals;
performance work is deliberately deferred (see [roadmap.md](roadmap.md)).

## The three stages, and why the seams are where they are

```
  .chroma file
       |
       |  Sdl/        lexer -> parser -> evaluator -> binders  [ChromaTest.Core]
       v
  Model/              Camera, lights, tree of Solid            [ChromaTest.Core]
       |
       |  Compilation/  flatten, binarise, bake transforms     [ChromaTest.Core]
       v
  GPU tape + tables    post-order instructions, matrices
       |
       |  Rendering/   texture buffer upload                   [ChromaTest]
       v
  raytrace.frag        stack machine over spans                [Shaders/]
```

The folder is `Model/` rather than `Scene/` for a dull but real reason: the aggregate type
is called `Scene`, and a class sharing its own namespace's name is a steady source of
resolution ambiguities at every use site.

Each arrow is a one-way dependency, and each stage is replaceable without touching its
neighbours. Three specific boundaries carry their weight:

**The parser knows no primitives.** `Sdl/Syntax` produces a uniform AST in which `sphere`,
`difference` and `camera` are just identifiers followed by blocks. Nothing in the lexer or
parser mentions geometry. That is what makes the language layer genuinely replaceable —
and it will be replaced, since the current dialect is provisional and will be reworked when
loops and macros arrive.

**Node names are resolved through a registry.** `Sdl/Binding` maps a name to an
`INodeBinder` in a `NodeBinderRegistry`. Adding a `cone` primitive is a `ConeBinder` class
and one registration line; the parser, the AST and the existing binders are untouched. This
is the main extension point of the project and the answer to "how do I add a shape".

**The scene model has no `Intersect` method.** It is pure data. The intersection algorithm
lives in GLSL and nowhere else, so there is exactly one implementation of it and no risk of
two drifting apart. The model is traversed through `ISolidVisitor<T>` instead: the hierarchy
printer is one visitor, the tape compiler is another, and a future CPU reference tracer
would be a third — none of them requiring a change to the solid classes.

## Projects

| Project | Kind | Depends on | Role |
| --- | --- | --- | --- |
| `src/ChromaTest.Core` | library | nothing but the BCL | language, scene model, GPU compilation |
| `src/ChromaTest` | exe | Core, Silk.NET | window, upload, shader, the actual render |
| `src/ChromaTest.SceneDump` | exe | Core | parses a scene and prints the hierarchy |
| `tests/ChromaTest.Core.Tests` | xUnit | Core | lexer, parser, evaluator, binding, diagnostics |

`ChromaTest.Core` having **no** Silk.NET reference is a constraint worth keeping. It is what
makes the parser and the compiler runnable and testable without a GL context, and what
makes `SceneDump` a fifty-line program. The test project is the proof it holds: it drives
the entire front end from strings, with no window anywhere.

`SceneDump` exists because parsing is worth verifying on its own. It is the deliverable of
iteration 1, and it stays useful afterwards as the tool that answers "did it read my file
the way I meant it".

## Data flow of a run

```
Program.Main(args)
   |
   |-- SceneLoader.TryLoad(path)                     CPU, once   [built]
   |      lex -> parse -> evaluate -> bind
   |      -> Scene { Camera, Light[], Solid[] }
   |      -> Diagnostic[]; any error and we exit before creating a window
   |
   |-- CsgTapeBuilder.Compile(scene)                 CPU, once
   |      post-order flatten, binarise n-ary operators,
   |      compose and invert transforms, collect materials,
   |      compute the span and stack budget
   |      -> CompiledScene { int[] tape, float[] prims, float[] materials }
   |
   |-- OnLoad: SceneBuffer uploads the three arrays as texture buffers
   |           camera and lights become uniforms
   |
   `-- OnRender: draw one fullscreen quad
          raytrace.frag, per pixel:
             build the primary ray from the camera uniforms
             run the tape as a stack machine  -> span list
             pick the first span with tOut > EPS
             recompute the normal from the surviving primitive
             shade, plus one shadow ray per light
```

The heavy work happens once, at load. A frame is one quad and one uniform update. Changing
the scene means re-running the CPU stages and re-uploading — never recompiling the shader,
which is exactly the property that makes hot-reloading the scene file cheap later.

## Why a data buffer rather than generated GLSL

The alternative was to emit specialised GLSL from the scene tree and compile it at load: no
stack, no interpreter, straight-line code. It was rejected.

| | Data buffer + interpreter | Generated GLSL |
| --- | --- | --- |
| Changing scene | re-upload a buffer | recompile a shader |
| The shader | one file, readable, diffable, debuggable | different for every scene, machine-written |
| Scene size limit | buffer size | shader program size and compile time |
| Per-frame cost | slightly higher — stack machine, `texelFetch` | slightly lower — fully unrolled |

At proof-of-concept scale the performance difference is not measurable, while the debugging
difference is enormous: a bug in a hand-written shader is a bug you can read.

## Why OpenGL 3.3 Core

Inherited from the boilerplate, and re-examined rather than assumed. The relevant question
was whether the scene buffer forces a version bump.

It does not. Shader storage buffers arrived in GL 4.3, but **texture buffer objects**
(`samplerBuffer`, `texelFetch`) have been core since GL 3.1 and do everything needed here:
large, integer-indexed, unfiltered arrays readable from a fragment shader. The cost is
manual decoding — one `vec4` per texel, so a 4x4 matrix is four fetches — and that cost is
paid once in a small helper.

Staying on 3.3 keeps the widest driver support, keeps macOS theoretically reachable, and
avoids a change that would have bought only syntactic convenience. Moving to 4.3 later, for
compute shaders, remains open.

## Coordinate and matrix conventions

Right-handed, `+X` right, `+Y` up, `+Z` towards the viewer.

`System.Numerics.Matrix4x4` stores elements row-major and its factory methods build matrices
for row-vector math (`v' = v * M`); GLSL reads uniform memory column-major and uses
column-vector math (`v' = M * v`). Uploading the raw bytes with `transpose: false` makes each
side read the other's transpose, and the two mismatches cancel exactly. The visible
consequence is that composition order is mirrored between the two languages.

Matrices that travel in the **texture buffer** rather than as uniforms are a different case:
they are decoded by hand with four `texelFetch` calls, so the row/column question is settled
by how the shader helper reassembles them, not by GL. The helper is the single place that
defines it, and it is documented in [implementation.md](implementation.md).

Transforms reaching the GPU are already **inverted and composed** — world to local, one
matrix per primitive, ancestors folded in. The shader never multiplies transforms; it only
applies them. See [csg-raytracing.md](csg-raytracing.md#transforms) for the two rules that
matter (do not renormalise the local direction; return normals through the inverse
transpose).

## Error handling posture

Two very different failure modes, handled differently.

**Scene errors are data errors**, and there will be many of them, written by a human in a
text editor. They are accumulated in a `DiagnosticBag` with file/line/column, reported all
at once, and the process exits non-zero without opening a window. A parser that stops at the
first mistake makes fixing a scene a game of twenty questions.

**Shader and GL errors are programmer errors**, and there should be none. They throw
immediately, with the driver's own log attached — a black window is far more expensive to
diagnose than a stack trace. This is inherited from the boilerplate's `Shader` class and
kept as is.

The span budget straddles the two: a scene that needs more spans than the shader's
compile-time arrays allow is a *data* error, reported as a diagnostic naming the offending
subtree. Silently truncating a span list would produce geometry that is subtly wrong in a
way indistinguishable from an algorithm bug.

## Extension points

| To add | Touch |
| --- | --- |
| A primitive | one `INodeBinder`, one `Solid` subclass, one span function and one normal function in GLSL |
| A CSG operator | one binder, one `Solid` subclass, one opcode, one merge function in GLSL |
| A light type | one binder, one `Light` subclass, one branch in the shading loop |
| A material property | the material table layout and the shading function |
| A different syntax | `Sdl/Lexing` and `Sdl/Syntax` only — the binders and everything below are unaffected |
| A CPU reference tracer | one new `ISolidVisitor<T>`; nothing existing changes |

## Where to read next

- [scene-language.md](scene-language.md) — the input format
- [csg-raytracing.md](csg-raytracing.md) — the algorithm and the GPU encoding
- [implementation.md](implementation.md) — per-file notes and pitfalls
- [roadmap.md](roadmap.md) — what is built, what is next
