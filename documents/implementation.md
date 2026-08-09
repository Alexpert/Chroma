# Implementation notes

Per-file notes and the traps worth knowing before changing something. Read
[architecture.md](architecture.md) first for the shape of the whole.

This document tracks the code that **exists**, and is updated at the end of each iteration.
See [roadmap.md](roadmap.md) for what is coming.

## File map

```
ChromaTest.sln
├── src/ChromaTest.Core/          language + model + compilation, no Silk.NET reference
│   ├── SceneLoader.cs            TryLoad / TryLoadCompiled -- the only public entry points
│   ├── Sdl/Source/               SourceText, SourceSpan, Diagnostic, DiagnosticBag
│   ├── Sdl/Lexing/               TokenKind, Token, Lexer
│   ├── Sdl/Syntax/               SyntaxNodes, Statements, Parser  (no node names here)
│   ├── Sdl/Binding/              SdlValue, Scope, Evaluator, BlockReader,
│   │   └── Binders/              BindingContext, NodeBinderRegistry, SceneBuilder
│   ├── Model/                    Scene, Camera, Lighting/, Materials/, Geometry/
│   └── Compilation/              GpuLayout, SpanBudget, CsgTapeBuilder, SceneCompiler
├── src/ChromaTest/               the renderer
│   ├── Program.cs                CLI, window lifecycle, per-frame uniforms
│   ├── Rendering/                Shader, FullscreenQuad, SceneBuffers
│   └── Shaders/                  raytrace.vert, raytrace.frag
├── src/ChromaTest.SceneDump/     Program, HierarchyPrinter, Format
├── tests/ChromaTest.Core.Tests/  front end, camera basis, compilation
├── scenes/                       primitives, csg, diagnostics-demo
└── documents/
```

Nothing of the original boilerplate is left: the cube, its shaders and the model/view/
projection pipeline are gone. `Rendering/Shader.cs` survived unchanged in substance — it
never had anything to do with cubes — and only gained uniform overloads.

## The front end, stage by stage

`SceneLoader.TryLoad` runs four stages over one file, all sharing a single
`DiagnosticBag`. Nothing throws; every stage reports and carries on, so one run surfaces
everything it can reach.

**Lexer.** One pass, no lookahead beyond two characters. An unrecognised character becomes
a `Bad` token *and* a diagnostic, and lexing continues — otherwise a single stray character
would hide every later problem.

**Parser.** Recursive descent over the EBNF in [scene-language.md](scene-language.md).
Every loop that can consume nothing carries an explicit progress guard: if an iteration
leaves the token index unchanged, it reports and advances one token. Without those guards
error recovery turns into a hang, which is the failure mode a parser must never have. A
value the parser cannot read becomes a `MissingExpression`, and later stages skip those
silently rather than piling a second complaint onto the same mistake.

**Evaluator.** Folds expressions to a `NumberValue`, `VectorValue` or `ObjectValue`,
resolving `let` bindings against a flat `Scope`. Object contents are evaluated eagerly.

**Binders.** `BindingContext` looks the node name up in `NodeBinderRegistry` and hands the
block to an `INodeBinder`. Two details carry most of the ergonomics:

- `BlockReader` marks each entry as it is consumed, and `ReportUnusedEntries` then
  complains about whatever is left. That is where `unknown field 'raduis' on 'sphere'`
  comes from — no binder has to enumerate what it does *not* accept. `BindingContext` calls
  it, not the binders, so none of them can forget.
- `SolidBinder` handles the modifiers every solid shares, so a new shape only describes its
  own geometry. Adding `cone` is one subclass and one line in `CreateDefault`.

### Two things worth not rediscovering

**Transform modifiers are read by walking the block in order**, not by looking each name up
independently. Order is semantic — `translate` then `rotate` swings a solid around the
origin, the reverse leaves it where the translation put it — so the parser preserves entry
order and `SolidBinder.ReadTransform` consumes it. There is a test for exactly this.

**Numbers are converted with `CultureInfo.InvariantCulture`**, in `Lexer.ReadNumber` and in
`SceneDump`'s `Format`. This is not pedantry: the current machine's culture uses a decimal
comma, so the default parse *rejects* `1.5` and the default format *prints* `<0,8 0,2 0,2>`.
The bug would appear on some machines and not others, which is the worst kind. A test
forces a `fr-FR` culture and reads `1.5` back.

## `src/ChromaTest/ChromaTest.csproj`

Three Silk.NET packages, all pinned to the same version — Silk.NET ships its modules in
lockstep and mixing versions produces binding mismatches:

- `Silk.NET.Windowing` — window creation and the event loop
- `Silk.NET.OpenGL` — the GL function bindings
- `Silk.NET.Input` — the input context

`<AllowUnsafeBlocks>true</AllowUnsafeBlocks>` is mandatory, not stylistic. `glBufferData`,
`glVertexAttribPointer`, `glDrawElements`, `glTexBuffer` and the `glUniform*` family all take
raw pointers in their C signatures and Silk.NET exposes them as such. There is no
pointer-free path.

The asset copy rule is what makes shader editing workable:

```xml
<None Update="Shaders\**\*" CopyToOutputDirectory="PreserveNewest" />
```

`PreserveNewest` compares timestamps and re-copies only changed files, so "edit the shader,
`dotnet run`" needs no other step. Editing the copy under `bin/` instead is the classic
mistake — the next build silently overwrites it.

There is deliberately **no** equivalent rule for `scenes/**`. A scene file is user data
named on the command line, not an asset shipped with the binary, so `SceneLoader` resolves
the path exactly as given, relative to the caller's working directory. This is the opposite
of the shader rule below, and the two are easy to confuse.

### Why the shader paths are absolute

`Program.cs` builds shader paths with `Path.Combine(AppContext.BaseDirectory, "Shaders",
...)` rather than the relative string `"Shaders/cube.frag"`.

A relative path resolves against the **current working directory**, which is not the
executable's folder. `dotnet run` happens to set the working directory to the project root,
so a relative path silently reads the *source* shaders and appears to work — but
double-clicking the `.exe` or launching from a debugger then throws `FileNotFoundException`
at startup. `AppContext.BaseDirectory` is where `PreserveNewest` put the copies, so the
program behaves identically however it is launched.

This applies to **shaders only**. Scene files are the opposite case, per the note above.

## `Rendering/Shader.cs`

Kept as is through the rewrite. It has nothing to do with cubes.

### Compilation sequence

```
File.ReadAllText(path)
glCreateShader(type)        -> handle
glShaderSource(handle, src)
glCompileShader(handle)
glGetShaderiv(handle, GL_COMPILE_STATUS)   -> 0 on failure
```

On failure it reads `glGetShaderInfoLog`, deletes the shader object, and throws with the log
embedded. The log is the driver's own text and carries line numbers relative to the source
file — it must reach the console unmodified. This matters far more for the ray tracing
shader than it did for the cube: `raytrace.frag` is where all the real logic lives.

### Linking sequence

```
glCreateProgram()                          -> program handle
glAttachShader(program, vertex | fragment)
glLinkProgram(program)
glGetProgramiv(program, GL_LINK_STATUS)    -> 0 on failure
glDetachShader / glDeleteShader  (both stages)
```

Link failures are distinct from compile failures: both stages compiled but disagree. Usual
causes are a mismatched varying between stages and a missing `main`. Detach-then-delete
after a successful link is the correct cleanup — `glDeleteShader` only flags the object,
which is freed once nothing references it. The failure paths delete the stage objects too,
so a broken shader leaks no handles.

### Uniform upload

Locations are cached in a dictionary. `glGetUniformLocation` is a string lookup into the
driver's symbol table and calling it every frame for a value that cannot change is wasteful.

A location of `-1` **throws**. It means either a misspelled name on the C# side, or — the
surprising case — that the shader never reads that uniform, so the compiler removed it
entirely. The declaration still being in the file changes nothing. The exception message
says so explicitly.

This will bite harder during iterations 2 and 3 than it did with the cube, because the ray
tracing shader will be edited with large parts commented out while debugging, and that is
exactly what strips uniforms. If a stubbed-out shader suddenly throws on a uniform that was
fine a minute ago, this is why.

Matrix uploads pass `transpose: false`. See the matrix conventions in
[architecture.md](architecture.md#coordinate-and-matrix-conventions); the short version is
that `System.Numerics` is row-major/row-vector, GLSL is column-major/column-vector, and the
two mismatches cancel exactly. Flipping this flag produces geometry that is invisible or
wildly distorted.

`(float*)&value` is safe without `fixed` because `value` is a struct parameter on the stack,
not a heap reference the GC can move.

### Overloads still missing

Only `SetUniform(string, Matrix4x4)` exists. Iteration 2 needs `float`, `int`, `Vector3`,
and array forms for the light uniforms. They follow the same location-caching path.

## `Program.cs`

### Window options

```csharp
options.API = new GraphicsAPI(
    ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.Default, new APIVersion(3, 3));
```

`ContextProfile.Core` matters: the compatibility profile silently accepts legacy
fixed-function calls that then behave differently across drivers. Core fails immediately.

`PreferredDepthBufferBits = 24` requests a depth attachment, needed by the cube. **The ray
tracer will not need it**: a fullscreen quad has no depth complexity, and visibility is
resolved analytically along each ray. Both the depth buffer request and
`glEnable(GL_DEPTH_TEST)` come out in iteration 2.

### The `using Shader = ChromaTest.Rendering.Shader;` alias

`Silk.NET.OpenGL` exports its own `Shader` type. With both namespaces imported the bare name
is ambiguous and the build fails with CS0104. Keep the alias.

### Resize

`OnFramebufferResize` does two things and both are required — `glViewport(size)`, else
rendering stays confined to the original rectangle, and rebuilding the projection, else the
aspect ratio is stale.

`UpdateProjection` guards `size.Y == 0`: minimising reports a zero-height framebuffer, and
`size.X / 0f` yields infinity, which propagates into the matrix and produces NaN from then
on — the image never comes back after restoring the window. The ray tracer inherits this
hazard exactly, since the camera basis is also built from the aspect ratio.

Framebuffer size and window size differ on high-DPI displays. The framebuffer size is the
one in pixels and the correct input for both calls.

## The renderer

### Texture buffer decoding, and the one place the matrix convention lives

The scene arrives in the shader through `samplerBuffer` / `isamplerBuffer` uniforms read
with `texelFetch` — one `vec4` or `ivec4` per texel, integer index, no filtering. GL 3.3
has no shader storage buffers, so this is how a scene of arbitrary size gets in.

A 4x4 matrix is four consecutive texels, and `fetchMatrix` in `raytrace.frag` is the **only**
definition of the row/column convention on that path:

```glsl
return mat4(r0, r1, r2, r3);
```

`mat4`'s constructor takes **columns**. `CsgTapeBuilder.AppendRows` writes the **rows** of a
`System.Numerics` matrix, which is row-major and row-vector. Passing rows to a
column-taking constructor yields the transpose — and the transpose of a row-vector matrix is
exactly the column-vector matrix for the same transform. The two conventions cancel, and
there is no `transpose()` to add anywhere. Note this is a *different* argument from the
`transpose: false` on the uniform path, which cancels for the same underlying reason but
through a different mechanism; do not merge the two in your head.

### Canonical primitives

The shader reads no shape parameters. Every primitive is evaluated as a unit sphere, a
`[-1, 1]` box, or a unit cylinder along `+Y` from `y = 0` to `y = 1`, and its real
dimensions live in the baked inverse matrix. Five texels per primitive: one header of
`(kind, materialIndex, 0, 0)`, then the four matrix rows.

A pleasant consequence: a non-uniform scale on the canonical sphere is an ellipsoid, for
free, with no code anywhere that knows what an ellipsoid is.

### No depth buffer

`PreferredDepthBufferBits` is not requested and `GL_DEPTH_TEST` is not enabled. A fullscreen
quad has no depth complexity, and visibility between solids is resolved analytically along
each ray. Adding a depth test back would do nothing except cost fill rate.

### Fixed-size arrays

`MAX_SPANS`, `MAX_STACK`, `MAX_TAPE` and `MAX_LIGHTS` are compile-time constants; GLSL 3.30
has no dynamic arrays. The CPU computes the real budget while flattening and rejects a scene
that exceeds it, with a diagnostic naming the subtree. Truncating silently produces geometry
that is wrong in a way that looks like an algorithm bug.

Watch the register pressure: the span stack is `MAX_STACK * MAX_SPANS` spans of four
components each. Raising either constant is not free, and a shader that spills will get
dramatically slower with no error message.

### The two transform rules

Both are easy to get wrong and both produce plausible-looking wrong images:

- **Do not renormalise the ray direction after transforming it into local space.** Under a
  scale, the non-unit length is what keeps `t` comparable with every other primitive's.
  Symptom of getting it wrong: one solid always drawn in front of another regardless of
  geometry.
- **Return normals through the inverse transpose**, `transpose(mat3(invM)) * nLocal`, not
  `mat3(invM) * nLocal`. The two agree for pure rotations, so this survives every test
  scene that has no scaling in it.

### The normal flip in `difference`

Surfaces contributed by a subtracted operand need their normal negated. It is carried as the
sign bit of the surface reference in a span, and applied once at the end when the normal is
recomputed. If a drilled cavity renders black or inside-out, check this before anything
else — it is the most commonly botched detail in a CSG renderer.

## Pitfalls checklist

Symptoms and their usual causes.

| Symptom | Cause |
| --- | --- |
| The scene renders mirrored left to right | The camera is at negative Z, so it looks down `+Z`. Right-handed space puts `+X` on the left from there — see [scene-language.md](scene-language.md#coordinate-system) |
| Scene loads but a field seems ignored | Misspelled field names are reported, not silently dropped — check stderr before suspecting the binder |
| A transform lands somewhere unexpected | Modifiers apply in written order; swap `translate` and `rotate` and see |
| Numbers print or parse with a comma | A conversion bypassed `CultureInfo.InvariantCulture` |
| Parser appears to hang | A loop lost its progress guard and is not consuming the token it cannot read |
| Black window, no error | Shader compiled but writes nothing to `FragColor`; or the camera is inside/behind everything |
| Exception: uniform not found | Name typo, or the shader stopped reading that uniform and the driver stripped it |
| Shader edits have no effect | Edited the copy under `bin/`, which the next build overwrites |
| `FileNotFoundException` on a shader at startup | A shader path was made relative; it must resolve against `AppContext.BaseDirectory` |
| `FileNotFoundException` on a scene file | The opposite mistake — scene files resolve against the working directory, not `BaseDirectory` |
| Geometry distorted or invisible after a matrix change | `transpose` flag flipped, or C#/GLSL multiplication order no longer mirrored |
| NaN, image never returns after minimising | Aspect ratio computed from a zero-height framebuffer |
| CS0104 on `Shader` | The `Silk.NET.OpenGL.Shader` collision; keep the alias in `Program.cs` |
| One solid always in front of another | Local ray direction renormalised after the inverse transform |
| Shading wrong only on scaled objects | Normal transformed by `mat3(invM)` instead of its transpose |
| Cavity of a `difference` renders black | Missing normal flip on subtracted surfaces |
| Speckles along CSG seams | Degenerate zero-width spans not dropped, or inconsistent `EPS` between the merge and the hit selection |
| Stippled acne on lit surfaces | Shadow ray not offset along the normal |
| Sudden large slowdown after raising a `MAX_*` constant | Span stack spilled out of registers |
