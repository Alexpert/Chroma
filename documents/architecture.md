# Architecture

## Purpose and scope

Chroma renders a scene described in a text file, by ray tracing CSG solids **on the
GPU**. The input is a `.chroma` file holding the camera, the lights and a tree of solids
built from primitives and boolean operators; the output is an image.

The distinction from POV-Ray, which is the obvious point of comparison, is where the work
happens. POV-Ray parses on the CPU and traces on the CPU. Here the CPU parses, validates and
*compiles* the scene — into **GLSL**, since iteration 12 — and a fragment shader does the
tracing. That split is the reason the architecture looks the way it does: everything upstream
of the GPU is ordinary, testable C# with no graphics dependency, and the compiler's output is
a string, which makes it as testable as everything before it.

Correctness and replaceable boundaries are the goals; performance work is deliberately
deferred (see [roadmap.md](roadmap.md)).

## The three stages, and why the seams are where they are

```
  .chroma file
       |
       |  Sdl/        lexer -> parser -> evaluator -> binders  [Chroma.Core]
       v
  Model/              Camera, lights, tree of Solid            [Chroma.Core]
       |
       |  Compilation/ find which roots are the same shape,    [Chroma.Core]
       |               peel their placements, build a BVH
       v
       |  Codegen/     bake transforms, size every span list,  [Chroma.Core]
       |               emit one function per leaf and per SHAPE
       v
  generated GLSL       + leaf/material tables for shading,
                         instance and node tables for placing
       |
       |  Rendering/   splice into raytrace.frag, compile,     [Chroma]
       |               upload the texture buffers
       v
  raytrace.frag        nested span calls, bounce loop          [Shaders/]
       |
       |  accumulation buffer, one sample per frame
       v
  resolve.frag         exposure, tone mapping, gamma           [Shaders/]
```

The folder is `Model/` rather than `Scene/` for a dull but real reason: the aggregate type
is called `Scene`, and a class sharing its own namespace's name is a steady source of
resolution ambiguities at every use site.

Each arrow is a one-way dependency, and each stage is replaceable without touching its
neighbours. Three specific boundaries carry their weight:

**The parser knows no primitives.** `Sdl/Syntax` produces a uniform AST in which `sphere`,
`difference`, `object` and `camera` are just identifiers followed by blocks. Nothing in the
lexer or parser mentions geometry. That is what makes the language layer genuinely
replaceable, and it has now been tested three times: control flow in iteration 8, functions
after it, and then the JavaScript revision that replaced the loop form, the conditional
expression and the function syntax outright. All three were confined to `Sdl/Lexing` and
`Sdl/Syntax` plus the evaluator — the model, the compiler and the shader were untouched, and
every sample scene came through the last one with a byte-identical hierarchy dump.

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
| `src/Chroma.Core` | library | nothing but the BCL | language, scene model, GPU compilation |
| `src/Chroma` | exe | Core, Silk.NET | window, upload, shader, the actual render |
| `src/Chroma.SceneDump` | exe | Core | parses a scene and prints the hierarchy |
| `tests/Chroma.Core.Tests` | xUnit | Core | lexer, parser, evaluator, binding, diagnostics |

`Chroma.Core` having **no** Silk.NET reference is a constraint worth keeping. It is what
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
   |-- SceneCompiler.Compile(scene)                  CPU, once   [built]
   |      recover shape identity: which roots are the same solid
   |         standing somewhere else (ShapeCanonicalizer)
   |      decide what to share, and what one program may weigh
   |         (ShapePartition.Choose, SceneChunker.Split)
   |      compose and invert transforms, collect materials,
   |         generate GLSL per chunk (GeometryEmitter)
   |      -> CompiledScene { CompiledChunk[] chunks,
   |                         float[] prims, materials, shapes,
   |                         instances, nodes }
   |
   |-- OnLoad: SceneBuffers uploads the tables                            [built]
   |           camera, lights and render settings become uniforms
   |           one shader program per chunk is compiled and linked
   |
   `-- OnRender                                                           [built]
          pass 1 -> the accumulation buffer

             one chunk: the MEGAKERNEL, a whole path in registers.
             raytrace.glsl, per pixel:
                spawnPath      a jittered primary ray from the camera
                for each bounce, up to render.maxBounces:
                   intersectPath  traceScene: each shape that stands
                                  alone, guarded, then a BVH walk over
                                  everything that repeats
                   shadeVertex    the nearest span, the normal, the
                                  material, emission, medium
                   connectDirect  sample the lights, one shadow ray each
                   bouncePath     sample the BRDF for the next direction
                average the result into everything accumulated so far

             several chunks: the WAVEFRONT, the same five functions over
             state in a buffer, one dispatch each. No single program
             holds the scene, so the path cannot be held in one either:
                spawn; for each bounce { intersect x chunks; shade;
                shadow x chunks; connect } gather

          pass 2 -> the window
             resolve.frag: exposure, ACES tone mapping, gamma
```

The heavy work happens once, at load. A frame is two quads and one uniform update. Changing the
scene means re-running the CPU stages and recompiling the shader — the property given up in
iteration 12, and the one nothing depended on, since the shader was already compiled once per
run.

The image is **progressive**: one sample per pixel per frame, averaged across frames, so it
opens noisy and converges while the camera is still. That is what keeps an interactive frame
cheap despite each pixel now tracing the scene several times.

## Why generated GLSL rather than a data buffer

**This decision was reversed in iteration 12.** The original is kept below because it was right
when it was made and the reason it stopped being right is the interesting part; the full
argument, the design and the measurements are in
[code-generation.md](code-generation.md).

> The alternative was to emit specialised GLSL from the scene tree and compile it at load: no
> stack, no interpreter, straight-line code. It was rejected.
>
> | | Data buffer + interpreter | Generated GLSL |
> | --- | --- | --- |
> | Changing scene | re-upload a buffer | recompile a shader |
> | The shader | one file, readable, diffable, debuggable | different for every scene, machine-written |
> | Scene size limit | buffer size | shader program size and compile time |
> | Per-frame cost | slightly higher — stack machine, `texelFetch` | slightly lower — fully unrolled |
>
> At the scene sizes reached so far the performance difference is not measurable, while the
> debugging difference is enormous: a bug in a hand-written shader is a bug you can read.

Two things in that table turned out to be wrong, and the third was answerable.

**"Slightly lower — fully unrolled" understated it by an order of magnitude,** because it
priced the wrong resource. The cost of one shader for all scenes is not the interpreter's
`texelFetch`es; it is that **every array in it is sized for the worst scene anyone might
write**, and that a fragment shader is bound by how much state a thread carries. `cornell.chroma`
is eight convex primitives and was carrying a four-deep stack of eight-span lists, a 32-slot
crossing array, a 24-slot sweep array, a 16-slot blob array and a quartic solver. Generated, it
holds one span. The measured range across `scenes/` is **2.1x to 17.1x**, with every image
unchanged.

**"Scene size limit: shader program size"** was the risk, and it is not the binding one:
`lattice.chroma` generates 11,885 lines, compiles inside the first frame, and is among the
biggest winners at 14.9x.

**"A bug in a hand-written shader is a bug you can read"** is still true, and it is why only the
*geometry* is generated. The path tracer — sampling, BRDF, lights, media, accumulation, tone
mapping, and the primitive maths itself — remains a hand-written file with a splice marker in
it. `--emit-shader` writes out exactly what the driver is handed, so a generated shader is also
a shader you can read.

What the reversal cost is the property recorded above as "changing scene: re-upload a buffer".
A scene now recompiles a shader. Nothing depended on it: the shader was already compiled once
per run, and hot-reload was never built.

## Why OpenGL 3.3 Core — now a tier rather than a target

Inherited from the boilerplate, and re-examined rather than assumed. The relevant question
was whether the scene buffer forces a version bump.

It does not. Shader storage buffers arrived in GL 4.3, but **texture buffer objects**
(`samplerBuffer`, `texelFetch`) have been core since GL 3.1 and do everything needed here:
large, integer-indexed, unfiltered arrays readable from a fragment shader. The cost is
manual decoding — one `vec4` per texel, so a 4x4 matrix is four fetches — and that cost is
paid once in a small helper.

Staying on 3.3 kept the widest driver support and avoided a change that would have bought only
syntactic convenience. That held for twelve iterations.

**What changed:** per-scene code generation put a ceiling on scene size that is not about
buffers at all — the driver refuses a program past roughly 65,000 assembly instructions, and a
chess set reached it. Whether a newer OpenGL lifts that ceiling was worth finding out, so the
renderer now asks for a 4.6 context and can run the tracer as a compute shader over storage
buffers. It does not; what moved the ceiling was instancing, which put repeated placements in a
buffer after all. The answer turned out to be about buffers, just not the ones this paragraph had
in mind.

**It does not lift it.** The same scene is refused at instruction 65,886 as a fragment shader
and 65,887 as a compute shader: NVIDIA lowers both stages through the same backend. The compute
path is implemented, correct and measured, and is opt-in behind `--compute` because it is also
not faster — a wash on eleven of thirteen scenes and 3.5× slower on the one with the heaviest
register load.

**And then it went.** Instancing moved the ceiling onto *distinct* shapes; a cost model gave the
compiler a number for what a shape weighs, so it could answer the question before the driver did;
and with a number, a scene too large for one program could be split into chunks and traced a stage
at a time. That last step needs the 4.6 context this section was opened to justify — storage
buffers for the ray state and an accumulation image to gather into — so the version question,
answered "it does not help" for the ceiling as originally posed, turned out to be answered
differently by the thing that removed it.

There are also **two** ceilings rather than the one this section named. Past a point the driver
stops reporting `too many instructions` and reports `error C5041: cannot locate suitable resource
to bind variable` instead, which is registers rather than instructions, and which scenes reach
depends on what they are made of rather than on how far over they are.

So 3.3 remains the default path for a scene that fits, rather than the only one, and the version
question is settled by measurement instead of assumption. [gpu-backends.md](gpu-backends.md)
records both ceilings, every attempt made against them, and what each one measured;
[instancing.md](instancing.md) records how a chunk is defined.

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
| A light type | one binder, one `Light` subclass, one branch in `directLight`, and a sampling routine if it has area |
| A material property | the material table layout, `fetchMaterial`, and the BRDF |
| A render setting | one field on `RenderSettings`, one line in `RenderBinder`, one uniform |
| A different syntax | `Sdl/Lexing` and `Sdl/Syntax` only — the binders and everything below are unaffected |
| A CPU reference tracer | one new `ISolidVisitor<T>`; nothing existing changes |

## Where to read next

- [scene-language.md](scene-language.md) — the input format
- [csg-raytracing.md](csg-raytracing.md) — the algorithm and the GPU encoding
- [lighting.md](lighting.md) — path tracing, the BRDF, sampling and convergence
- [transparency.md](transparency.md) — refraction, absorption, caustics, and the limits
- [implementation.md](implementation.md) — per-file notes and pitfalls
- [roadmap.md](roadmap.md) — what is built, what is next
