# Implementation notes

Per-file notes and the traps worth knowing before changing something. Read
[architecture.md](architecture.md) first for the shape of the whole.

This document tracks the code that **exists**, and is updated at the end of each iteration.
See [roadmap.md](roadmap.md) for what is coming.

## File map

```
Chroma.sln
├── src/Chroma.Core/          language + model + compilation, no Silk.NET reference
│   ├── SceneLoader.cs            TryLoad / TryLoadCompiled -- the only public entry points
│   ├── Sdl/Source/               SourceText, SourceSpan, Diagnostic, DiagnosticBag
│   ├── Sdl/Lexing/               TokenKind, Token, Lexer
│   ├── Sdl/Syntax/               SyntaxNodes, Statements, Parser  (no node names here)
│   ├── Sdl/Binding/              SdlValue, Scope, Evaluator, BlockReader,
│   │   └── Binders/              BindingContext, NodeBinderRegistry, SceneBuilder
│   ├── Model/                    Scene, Camera, RenderSettings, Lighting/, Materials/,
│   │                             Geometry/
│   └── Compilation/              GpuLayout, SpanBudget, CsgTapeBuilder, SceneCompiler
├── src/Chroma/               the renderer
│   ├── Program.cs                CLI, window lifecycle, the two render passes
│   ├── Rendering/                Shader, FullscreenQuad, SceneBuffers, AccumulationBuffer
│   └── Shaders/                  raytrace.vert, raytrace.frag, resolve.frag
├── src/Chroma.SceneDump/     Program, HierarchyPrinter, Format
├── tests/Chroma.Core.Tests/  front end, camera basis, compilation, render settings
├── scenes/                       primitives, shapes, sweeps, csg, cornell, glass,
│                                 lattice, colonnade, fog, diagnostics-demo
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

**Evaluator.** Runs the statements and folds the expressions between them, resolving
bindings against a `Scope` chained one frame per block, per control-flow body and per loop
iteration. Object contents are evaluated eagerly. Three things in here can fail to
terminate and each is budgeted rather than trusted: loop iterations (100 000 per load),
function calls (100 000), and call depth (64, since the evaluator recurses on the CLR stack
and an overflow there cannot be reported at all).

**Binders.** `BindingContext` looks the node name up in `NodeBinderRegistry` and hands the
block to an `INodeBinder`. Two details carry most of the ergonomics:

- `BlockReader` marks each entry as it is consumed, and `ReportUnusedEntries` then
  complains about whatever is left. That is where `unknown field 'raduis' on 'sphere'`
  comes from — no binder has to enumerate what it does *not* accept. `BindingContext` calls
  it, not the binders, so none of them can forget.
- `SolidBinder` handles the modifiers every solid shares, so a new shape only describes its
  own geometry. Adding `cone` is one subclass and one line in `CreateDefault`. `object` is
  that same subclass over a single child: it builds a `Union` of one operand, which *is* that
  operand, so nothing downstream of the binder knows the node exists.

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

## `src/Chroma/Chroma.csproj`

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

This bites during shader debugging, because `raytrace.frag` gets edited with large parts
commented out, and that is exactly what strips uniforms. If a stubbed-out shader suddenly
throws on a uniform that was fine a minute ago, this is why.

Matrix uploads pass `transpose: false`. See the matrix conventions in
[architecture.md](architecture.md#coordinate-and-matrix-conventions); the short version is
that `System.Numerics` is row-major/row-vector, GLSL is column-major/column-vector, and the
two mismatches cancel exactly. Flipping this flag produces geometry that is invisible or
wildly distorted.

`(float*)&value` is safe without `fixed` because `value` is a struct parameter on the stack,
not a heap reference the GC can move.

### Array overloads

The light uniforms are arrays, and `glGetUniformLocation` wants the name of an *element*:
the overloads look up `$"{name}[0]"` and upload `count` entries from there. Asking for the
bare array name returns `-1` on most drivers, which the `-1` rule above then turns into an
exception that reads like a typo.

## `Program.cs`

### Window options

```csharp
options.API = new GraphicsAPI(
    ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.Default, new APIVersion(3, 3));
```

`ContextProfile.Core` matters: the compatibility profile silently accepts legacy
fixed-function calls that then behave differently across drivers. Core fails immediately.

No depth buffer is requested and no depth test is enabled — see [No depth
buffer](#no-depth-buffer) below.

### The `using Shader = Chroma.Rendering.Shader;` alias

`Silk.NET.OpenGL` exports its own `Shader` type. With both namespaces imported the bare name
is ambiguous and the build fails with CS0104. Keep the alias.

### Resize

`OnFramebufferResize` does two things and both are required — `glViewport(size)`, else
rendering stays confined to the original rectangle, and rebuilding the camera basis, else
the aspect ratio is stale.

`UpdateRayBasis` guards `size.Y == 0`: minimising reports a zero-height framebuffer, and
`size.X / 0f` yields infinity, which propagates into the basis and produces NaN directions
from then on — the image never comes back after restoring the window.

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

Every primitive is evaluated in a canonical form — a unit sphere, a `[-1, 1]` box, a unit
cylinder along `+Y` from `y = 0` to `y = 1`, and so on — with its real dimensions in the baked
inverse matrix. Five texels per primitive: one header of
`(kind, materialIndex, paramA, paramB)`, then the four matrix rows.

A pleasant consequence: a non-uniform scale on the canonical sphere is an ellipsoid, for
free, with no code anywhere that knows what an ellipsoid is.

The two parameter slots were empty until iteration 6, and the shader genuinely read no shape
parameters at all. That is no longer true, and the reason is worth keeping straight, because
it looks like a regression and is not:

- A **cone**'s taper and a **torus**'s minor radius are *ratios*. Scaling changes both radii
  together, so no affine map can absorb them, and one number each has to travel alongside the
  matrix.
- A **prism**, **lathe** or **blob** is defined by a *list*. That list goes to a fourth
  texture buffer, `uShapes`, and the two slots hold an offset and a count into it.

Everything that *is* affine still goes in the matrix — a prism's height, a lathe's placement —
which is why one contour in the buffer serves a prism of any size.

`GpuLayout.SpansFor` is the other thing iteration 6 changed here. A leaf used to be worth one
span because every primitive was convex; five of the ten are not. Iteration 7 then **clamped**
those bounds to `MAX_SPANS`, which removed the leaf-level overflow check entirely — a clamped
bound cannot exceed the budget, so the check could never fire. The bounds, the clamp and what
it gives up are in
[csg-raytracing.md](csg-raytracing.md#fixed-size-arrays-and-the-span-budget).

### The register ceiling — read this before raising any `MAX_*`

The shader is roughly one step from the largest program the driver will accept, and the two
kinds of array in it cost wildly different amounts. Measured on a GeForce RTX 4070 SUPER:

| Change | Effect |
| --- | --- |
| `MAX_SPANS` 8 → 9 | −8% sample rate |
| `MAX_SPANS` 9 → 10 | **link fails**, `too many temporaries` |
| `MAX_CROSSINGS` 16 → 32 | no measurable cost |

A span list is multiplied by `MAX_STACK` and lives across the whole tape walk; a crossing array
is one local array inside one function. Both are counted — the compiler inlines `runTape` into
`trace` and `occluded`, and everything below it with them — but not at the same weight.

Two practical consequences:

- **A failed link here is not a syntax error.** `too many temporaries`, or
  `cannot locate suitable resource to bind variable … Possibly large array`, means the program
  is too big, and the fix is a smaller array rather than a corrected line.
- **Each array is sized for what it needs**, not from one shared constant. Adding
  `sphereSweep` broke the link at `MAX_CROSSINGS` 48 purely because it holds two parallel
  arrays; the working set — crossings 32, sweep events 24, blob events 16 — was found by
  bisection and should be re-bisected if another primitive is added.

### The polynomial solvers

`raytrace.frag` carries a Ferrari quartic solver, shared by the torus and the blob. Three
things about it are not in the textbook statement and all three were found by rendering
something wrong:

1. **Re-origin the ray before forming coefficients.** They grow as a power of the origin's
   distance while the roots stay near the object.
2. **Verify Ferrari's `βγ == r` identity** and fall back to the biquadratic factorisation when
   it fails. It fails whenever `q` is zero, because the resolvent's root is then a difference
   of two nearly equal cube roots and `sqrt` amplifies its noise.
3. **Guard the Newton polish** so it can only refine, never jump.

Each is written up with its symptom in
[csg-raytracing.md](csg-raytracing.md#solving-the-quartic). Skipping any of them produces an
image that looks like a geometry bug and is not one.

### Two passes, and the ping-pong between them

A frame is no longer one draw call. `raytrace.frag` renders into a floating-point
framebuffer, averaging one new sample into everything accumulated so far; `resolve.frag`
then tone maps that buffer to the window. The order inside `OnRender` is fixed and
`AccumulationBuffer` is named for it:

```
1. trace into WriteFramebuffer, sampling HistoryTexture
2. resolve to the screen, sampling ResultTexture
3. Advance()      <- only now does this frame become the next frame's history
```

Three things that are easy to get wrong:

- **A running average, not a sum over a count.** `mix(history, sample, 1/(n+1))` stays in the
  range of the values; a growing sum loses precision in a 32-bit float long before a long
  render finishes.
- **`Reset()` on anything that changes what a sample means** — currently a resize, and later
  any camera or scene change. Skipping it leaves a ghost of the old state fading slowly
  instead of vanishing.
- **The accumulation textures need `NEAREST` and `CLAMP_TO_EDGE`.** Leaving the default
  mipmap filter makes the texture incomplete, and an incomplete texture reads as black —
  indistinguishable from a shader that writes nothing.

The history is on **texture unit 4**: units 0 to 3 carry the scene buffers. It was unit 3
until `uShapes` was added, and moving it is exactly the kind of change that is invisible until
one of the two samplers reads the other's data.

`resolve.frag` reuses `raytrace.vert` rather than adding a second vertex shader. It already
outputs clip-space coordinates, and the UV is one line away from them.

### No depth buffer

`PreferredDepthBufferBits` is not requested and `GL_DEPTH_TEST` is not enabled. A fullscreen
quad has no depth complexity, and visibility between solids is resolved analytically along
each ray. Adding a depth test back would do nothing except cost fill rate.

### The stack machine, and where a root begins

`runTape` in `raytrace.frag` is one loop over the tape with an explicit stack of span lists,
because GLSL has no recursion. Three things about it are worth knowing before touching it.

**`OP_END_ROOT` closes a top-level solid.** Roots are implicitly unioned, but each is
*resolved* on its own rather than merged, which is what makes the span budget a per-root
limit — a scene may hold any number of separate solids however tight `MAX_SPANS` is. The
cost is one honest edge case: a ray starting inside two overlapping roots at once sees
whichever it leaves first, where a true union would show where it leaves the merged region.
Wrapping those solids in an explicit `union` removes it.

**`anyHit` is the same machine asking a different question.** A shadow ray does not want the
nearest hit, only whether anything overlaps `(EPS, distanceToLight)`, so it returns at the
first root that occludes. It deliberately skips the "started inside" rule — a surface must
not shadow itself. Sharing one function rather than writing a second loop is the point: the
stack machine is the part that must not drift into two versions.

**`difference` allocates a temporary.** It is `A ∩ complement(B)`, and the complement is
materialised into one more `SpanList`. That fits without a special budget rule because every
subtree yields at least one span, so `|B| + 1 <= |A| + |B|` — the count already reserved for
the result.

### Fixed-size arrays

`MAX_SPANS`, `MAX_STACK` and `MAX_LIGHTS` are compile-time constants; GLSL 3.30 has no
dynamic arrays. They are mirrored in `GpuLayout` so the CPU can compute the real budget while
flattening and reject a scene that exceeds it, with a diagnostic naming the innermost
offending subtree. Truncating silently produces geometry that is wrong in a way that looks
like an algorithm bug. `GpuLayout.MaxInstructions` is a different kind of limit: a CPU-side
sanity cap, since the tape lives in a buffer and needs no array.

Watch the register pressure: the span stack is `MAX_STACK * MAX_SPANS` spans of four
components each, and the operators take their operands **by value** — GLSL `in` parameters
are copies. Raising either constant is not free, and a shader that spills will get
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

### The path tracer

`documents/lighting.md` is the reference; these are the parts that bite while editing the
shader.

**The seed is threaded by hand.** GLSL has no global mutable state, so `inout uint` travels
down every function that draws a number. Copying it instead of advancing it produces
correlated noise, which does not look like noise — it looks like banding, and reads as a
geometry bug.

**`D` cancels out of the specular sampling weight.** The sampled weight is
`F * G * (v·h) / ((n·v)(n·h))`, with no `D` in it at all: it cancels exactly against the pdf
of the half-vector it was drawn from. If `distributionGgx` appears in `sampleBrdf`, that is
a bug, not an optimisation someone forgot.

**`BACKGROUND` is a light.** A ray that escapes brings its colour back as radiance, so it
behaves as a uniform environment light rather than as a backdrop. It is black, which means
the only light in a scene is what the file declares. There is deliberately no `AMBIENT`
constant any more: it stood in for light arriving from other surfaces, which the bounce loop
now computes for real.

**Emissive solids are not sampled by next-event estimation**, because a CSG solid has no
parameterisation to sample. The upside is that no path is ever counted twice, so there is no
multiple importance sampling anywhere — an absence a reviewer would otherwise read as an
oversight. It is also what makes a caustic reachable at all: a light is not geometry and can
never be hit, an emissive solid is and can.

### Refraction

`documents/transparency.md` is the reference; four things bite while editing.

**The microfacet check must not kill the diffuse lobe.** `sampleBrdf` draws one half-vector
from `D` and uses it for both specular lobes. When `v·h ≤ 0` that facet is invisible and
neither specular lobe exists — but the diffuse lobe does not go through `h` at all. Ending
the path there instead of just zeroing `pReflect` costs a matte surface most of its samples:
on `cornell.chroma` it measured **7% of the overall brightness** when it was wrong, and 12%
on the floor, with every region losing energy. A deficit in the same direction across
unrelated surfaces is the signature.

**The diffuse lobe is evaluated at its own half-vector.** `h` drawn from `D` may be used to
*choose* which lobe to sample; the diffuse lobe must then be *evaluated* with
`normalize(v + l)` for the `l` actually drawn, or the estimator is no longer unbiased.

**The offset flips after transmission.** `point - normal * SHADOW_BIAS`, because the ray is
now on the other side. The wrong sign re-hits the face just crossed and the path dies —
glass renders perfectly black, which reads as an absorption bug.

**`transmission` and `1 - transmission` never appear in a weight.** They cancel exactly
against the probabilities of the sub-choice that selects between transmitting and scattering,
just as `F` cancels against `pReflect`. Anything left of `D`, `eta` or the refraction
denominator in a sampling weight is a derivation that was not carried through.

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
| Stippled acne on lit surfaces | Shadow ray not offset along the normal, or a bias smaller than the rounding at that `t` |
| Sudden large slowdown after raising a `MAX_*` constant | Span stack spilled out of registers |
| A solid disappears entirely under an operator | Operand order: `difference` subtracts every operand from the *first* |
| Everything is in shadow | The point light's `maxT` was dropped, so occluders behind the light count |
| Two overlapping solids look wrong from inside them | Separate roots are resolved separately; wrap them in an explicit `union` |
| A scene is rejected for spans it clearly does not need | The budget is a worst case over all ray directions, not this one — it cannot depend on the ray |
| Noise that looks like banding or a repeating pattern | The RNG seed was copied instead of advanced, or seeded from pixel and frame added rather than hashed |
| The image never settles, or keeps a ghost of a previous state | `AccumulationBuffer.Reset()` was not called after something that changes what a sample means |
| A permanent bright or black pixel that never averages out | One `NaN` or `inf` entered the history; guard every division by a pdf |
| Isolated bright specks that fade only very slowly | Fireflies — a near-specular sample landing on a light. Lower `FIREFLY_CLAMP`, knowing it is biased |
| Everything is far too dark, and raising `intensity` looks wrong | The gamma step was skipped in the resolve pass |
| Glass renders perfectly black | The post-transmission ray offset kept the `+normal` sign, so the ray re-hits the face it just crossed |
| A dark rim on a glass silhouette | Schlick's Fresnel fed the incident angle from inside the dense medium; it needs the *transmitted* angle there |
| Glass gets **lighter** as it thickens | `absorption` applied at the surface instead of over the following segment, or applied as a multiplier rather than in an exponential |
| A glass sphere shows a black interior | `maxBounces` too low — crossing one sphere already spends two |
| Every surface is uniformly dimmer than it should be | A rejection test in `sampleBrdf` is ending paths the diffuse lobe would have carried; a *uniform* deficit across unrelated materials points here, not at a light |
| No caustic under a glass sphere | Expected with a `pointLight`: a light is not geometry, so a refracted path can never land on one. Use an emissive solid |
| Two overlapping glass solids show an internal lens-shaped seam | They are separate top-level roots, resolved separately. Wrap them in an explicit `union` to merge the spans |
| Frosted glass converges far slower lit from behind than from the front | Expected: the transmission lobe is not in `evalBrdf`, so light sampling never goes through a surface |
| Bright regions are flat white blobs | Tone mapping was skipped and the values clipped |
| A metal solid renders nearly black | Correct: a metal has no diffuse lobe and reflects its surroundings, and `BACKGROUND` is black. Give it something to reflect |
| One face of a prism is black while its neighbour is lit | Usually the same cause: that face points away from every light and `BACKGROUND` is black. Render it from a direction where its silhouette is unambiguous before suspecting the geometry |
| A blob is wrapped in a shell, or an onion of shells | The quartic solver returned values that are not roots. Check Ferrari's `βγ == r` identity, and that the Newton polish is guarded against jumping near a double root |
| A torus is ragged, or a blob's surface is quantised | The quartic's coefficients were built at the ray's origin instead of near the object; four orders of magnitude of a 32-bit float go into them before the solve begins |
| A band of a lathe or prism can be seen straight through | A vertex counted twice, flipping the parity of every crossing after it. Segment ranges must be half-open, so each edge owns its starting vertex and not its ending one |
| A prism or lathe renders inside out, unlit everywhere | The contour's perpendicular took the wrong sign — the even-odd point-in-contour test that decides it is what makes the winding of the file irrelevant |
| A scene with a prism, lathe or blob renders as noise | `uShapes` and the accumulation history are on the same texture unit; the scene buffers take 0 to 3 |
| Shader fails to link: `too many temporaries` or `Possibly large array` | Not a syntax error — a `MAX_*` was raised past what the driver will allocate. See [the register ceiling](#the-register-ceiling--read-this-before-raising-any-max_) |
| A Bézier lathe is smooth in outline but banded in shading | Normal blending is off — no step count fixes it, because the facets are in the normals, not the geometry |
| A hand-written lathe lost its sharp edges | The opposite: blending is on. It is carried in the sign of the segment count and belongs only to `spline: "bezier"` |
| A sphere sweep is pinched at every joint | The tangent cone was drawn between the two centres instead of between the two tangent circles, so it cuts into both caps |
| A tapering sweep is lit as though it were a cylinder | The normal was taken as radial. It points away from the centre of the *generating* sphere, which tilts it off radial by the cone's half-angle |
| Everything got dimmer after iteration 4 | Point lights now fall off with the square of the distance; intensities need to grow accordingly |
| The accumulation buffer reads as black | Its texture kept the default mipmap filter and is incomplete; it needs `NEAREST` |
| The scene has black bars down the sides | The camera sees past the geometry, and escaping rays return the black environment |
