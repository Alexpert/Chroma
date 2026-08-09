# Implementation notes

A per-file walkthrough of what the code does and why. Read
[architecture.md](architecture.md) first for the shape of the whole; this document covers
the details you need when changing something.

## File map

| Path | Role |
| --- | --- |
| `ChromaTest.csproj` | target framework, Silk.NET packages, shader copy rule |
| `Program.cs` | window setup, GL lifecycle, per-frame draw |
| `Rendering/Shader.cs` | shader compilation, linking, uniform upload |
| `Rendering/Cube.cs` | cube vertex data and GPU buffer setup |
| `Shaders/cube.vert` | vertex stage |
| `Shaders/cube.frag` | fragment stage |

## `ChromaTest.csproj`

Three packages, all pinned to the same version — Silk.NET ships its modules in lockstep and
mixing versions produces binding mismatches:

- `Silk.NET.Windowing` — window creation and the event loop
- `Silk.NET.OpenGL` — the GL function bindings
- `Silk.NET.Input` — the input context (used only for Escape-to-close)

`<AllowUnsafeBlocks>true</AllowUnsafeBlocks>` is mandatory, not stylistic. `glBufferData`,
`glVertexAttribPointer`, `glDrawElements` and `glUniformMatrix4fv` all take raw pointers in
their C signatures, and Silk.NET's bindings expose them as such. There is no pointer-free
path for these four.

The shader copy rule:

```xml
<None Update="Shaders\**\*" CopyToOutputDirectory="PreserveNewest" />
```

`PreserveNewest` compares timestamps and re-copies only changed files. This is what makes
"edit the shader, `dotnet run`" work: the build step notices the newer source file and
refreshes the copy in `bin/Debug/net8.0/Shaders/` that the program actually reads.

Editing the copy under `bin/` instead is the classic mistake — the next build silently
overwrites it.

### Why the paths are absolute

`Program.cs` builds the shader paths with
`Path.Combine(AppContext.BaseDirectory, "Shaders", "cube.frag")` rather than passing the
relative string `"Shaders/cube.frag"`.

A relative path is resolved against the **current working directory**, which is not the
executable's folder. `dotnet run` happens to set the working directory to the project root,
so a relative path silently reads the *source* shaders and appears to work — but
double-clicking the `.exe`, launching it from a debugger, or running it from any other
directory then throws `FileNotFoundException` at startup.

`AppContext.BaseDirectory` is the folder the assembly was loaded from, which is exactly
where `PreserveNewest` puts the shader copies. The program then behaves identically however
it is launched.

## `Rendering/Shader.cs`

### Compilation sequence

`CompileStage` runs the standard five-call sequence per stage:

```
File.ReadAllText(path)
glCreateShader(type)        -> handle
glShaderSource(handle, src)
glCompileShader(handle)
glGetShaderiv(handle, GL_COMPILE_STATUS)   -> 0 on failure
```

On failure it reads `glGetShaderInfoLog`, deletes the shader object, and throws with the
log embedded. The log is the driver's own text and includes line numbers relative to the
`.vert`/`.frag` file, which is the only reason shader editing is workable — it must reach
the console unmodified.

### Linking sequence

```
glCreateProgram()                    -> program handle
glAttachShader(program, vertex)
glAttachShader(program, fragment)
glLinkProgram(program)
glGetProgramiv(program, GL_LINK_STATUS)   -> 0 on failure
glDetachShader / glDeleteShader  (both stages)
```

Link failures are distinct from compile failures: both stages compiled, but they disagree.
The usual causes are a mismatched varying (an `out vec3 vNormal` in the vertex stage with
no matching `in vec3 vNormal` in the fragment stage, or a type mismatch between them) and a
missing `main`. The link log names the offending symbol.

Detach-then-delete after a successful link is the correct cleanup: `glDeleteShader` only
flags the object, and it is freed once nothing references it. The failure paths delete the
stage objects too, so a broken shader leaks no handles.

### Uniform upload

`SetUniform(string, Matrix4x4)` caches locations in a dictionary. `glGetUniformLocation`
is a string lookup into the driver's symbol table, and calling it three times per frame
forever is wasteful for a value that cannot change over a program's lifetime.

A location of `-1` throws. It means one of two things:

1. The name is misspelled on the C# side.
2. The shader does not read that uniform, so the compiler removed it entirely.

Case 2 is the one that surprises people. Comment out the use of `uModel` in the vertex
shader and the uniform ceases to exist, even though the declaration is still in the file.
The exception message says so explicitly.

The `transpose` argument is `false`. See the matrix conventions section in
[architecture.md](architecture.md) for the full reasoning; the short version is that
`System.Numerics` is row-major/row-vector, GLSL is column-major/column-vector, and the two
mismatches cancel exactly. Flipping this flag to `true` produces a cube that is either
invisible or wildly distorted — if that happens after a change here, this is the first
thing to check.

`(float*)&value` is safe without `fixed` because `value` is a struct parameter on the
stack, not a heap reference the GC can move.

## `Rendering/Cube.cs`

### Why 24 vertices for 8 corners

A cube has 8 distinct corner positions, but each corner is shared by three faces pointing
in three different directions. Vertex attributes are interpolated per-vertex, so a shared
corner can carry only one normal — and any single normal is wrong for two of its three
faces, producing smooth gradients across the edges instead of flat faces.

Duplicating each corner once per face gives 6 x 4 = 24 vertices, each with the flat normal
of the face it belongs to. Edges then stay crisp. The cost is 16 extra vertices, which is
nothing.

The 36-entry index buffer then stitches those 24 vertices into 6 x 2 = 12 triangles.

### Vertex layout

Interleaved, one contiguous block per vertex:

| Attribute | Location | Components | Type | Offset | Stride |
| --- | --- | --- | --- | --- | --- |
| `aPosition` | 0 | 3 | float | 0 | 24 bytes |
| `aNormal` | 1 | 3 | float | 12 | 24 bytes |

Stride is `6 * sizeof(float)` = 24 bytes. Interleaved beats separate arrays here because
a vertex's position and normal are fetched together, so they should share a cache line.

The `location` values are not incidental — they are hard-wired to the
`layout (location = 0)` and `layout (location = 1)` qualifiers in `cube.vert`. Adding a UV
attribute means adding it at location 2 in *both* files, and updating the stride and both
existing offsets.

### Buffer setup order

Order matters, because GL is a state machine and a VAO records the state that is bound
while it is itself bound:

```
glGenVertexArrays / glBindVertexArray      <- must be bound FIRST
glGenBuffers / glBindBuffer(GL_ARRAY_BUFFER)
glBufferData(GL_ARRAY_BUFFER, ...)
glGenBuffers / glBindBuffer(GL_ELEMENT_ARRAY_BUFFER)
glBufferData(GL_ELEMENT_ARRAY_BUFFER, ...)
glVertexAttribPointer(0, ...) / glEnableVertexAttribArray(0)
glVertexAttribPointer(1, ...) / glEnableVertexAttribArray(1)
glBindVertexArray(0)
```

The VAO captures the attribute pointers *and* the element buffer binding, but **not** the
`GL_ARRAY_BUFFER` binding — that one is only read at the moment `glVertexAttribPointer` is
called, which is why the vertex buffer must be bound before those calls and can be safely
unbound afterwards.

The element buffer binding is deliberately **not** reset to 0 before unbinding the VAO.
Unbinding `GL_ELEMENT_ARRAY_BUFFER` while the VAO is bound would erase the association the
VAO just recorded, and `Draw` would then read indices from nothing.

`fixed (float* v = Vertices)` pins the managed array so the GC cannot move it while the
driver copies from it. `glBufferData` copies synchronously, so the pin only needs to span
the call.

### Drawing

```csharp
glBindVertexArray(_vao);
glDrawElements(GL_TRIANGLES, 36, GL_UNSIGNED_INT, (void*)0);
glBindVertexArray(0);
```

The final `(void*)0` is a byte offset into the bound element buffer, not a pointer to
index data — a legacy of the same entry point serving both client-side and buffer-backed
index arrays. It stays 0 to draw from the start.

Winding is counter-clockwise for all 12 triangles as seen from outside the cube, which
matches GL's default front-face convention. Face culling is not enabled, so a mistake here
is currently invisible — but it would show up the moment `glEnable(GL_CULL_FACE)` is added.

## `Program.cs`

### Window options

```csharp
options.API = new GraphicsAPI(
    ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.Default, new APIVersion(3, 3));
options.PreferredDepthBufferBits = 24;
```

`ContextProfile.Core` matters: the compatibility profile would silently accept legacy
fixed-function calls, which would then behave differently across drivers. Core fails
immediately instead.

`PreferredDepthBufferBits = 24` requests a depth attachment. Without it there may be no
depth buffer at all, and enabling `GL_DEPTH_TEST` against a nonexistent buffer leaves the
cube drawn in submission order — back faces painting over front faces.

### The `using Shader = ChromaTest.Rendering.Shader;` alias

`Silk.NET.OpenGL` exports its own `Shader` type. With both namespaces imported, the bare
name is ambiguous and the build fails with CS0104. The alias resolves it without renaming
our type to something less obvious.

### Frame

```csharp
glClear(GL_COLOR_BUFFER_BIT | GL_DEPTH_BUFFER_BIT);
shader.Use();
shader.SetUniform("uModel", Model);
shader.SetUniform("uView", View);
shader.SetUniform("uProjection", _projection);
cube.Draw();
```

Both clear bits are required. Clearing colour but not depth leaves last frame's depth
values in place; the second frame then fails the depth test almost everywhere and the cube
disappears after the first frame — a distinctive symptom worth recognising.

Uniforms are set after `Use()`, never before: `glUniform*` writes into the program that is
current at that moment.

### Matrices

```csharp
Model      = CreateRotationY(30°) * CreateRotationX(20°)
View       = CreateLookAt((0, 0, 3), origin, +Y)
Projection = CreatePerspectiveFieldOfView(π/4, aspect, 0.1f, 100f)
```

The fixed 30°/20° rotation exists purely so the cube reads as a solid. Face-on, a cube is
a square, and a static face-on cube is indistinguishable from a bug. This orientation shows
three faces at once.

The camera distance of 3 with a 45° vertical FOV frames a unit cube with comfortable
margin.

Near and far planes are 0.1 and 100. Depth precision is distributed non-linearly and is
governed by the *ratio* far/near, so pushing near down toward 0.001 to "see closer" is what
causes z-fighting — it is not the far plane's fault.

### Resize

`OnFramebufferResize` does two things, and both are required:

```csharp
glViewport(size);           // else rendering stays confined to the original rectangle
UpdateProjection(size);     // else the aspect ratio is stale and the cube stretches
```

`UpdateProjection` guards `size.Y == 0`. Minimising a window reports a zero-height
framebuffer, and `size.X / 0f` yields infinity, which propagates into the projection matrix
and produces NaN positions from then on — the cube never comes back after restoring the
window.

Note that the framebuffer size and the window size differ on high-DPI displays. The
framebuffer size is the one in pixels, and it is the correct input for both calls.

## Shaders

`cube.vert`:

```glsl
vNormal = mat3(uModel) * aNormal;
gl_Position = uProjection * uView * uModel * vec4(aPosition, 1.0);
```

`mat3(uModel)` drops the translation row so the normal is rotated but not moved.
Transforming normals correctly under a general transform requires the inverse-transpose of
the upper-left 3x3; that reduces to the 3x3 itself when the transform is a pure rotation,
as it is here. Introducing non-uniform scaling into the model matrix breaks this and would
require computing the normal matrix on the CPU and passing it as a fourth uniform.

`cube.frag`:

```glsl
FragColor = vec4(normalize(vNormal) * 0.5 + 0.5, 1.0);
```

Normals live in `[-1, 1]` and colours in `[0, 1]`, so the `* 0.5 + 0.5` remap makes each
face's direction directly visible as a colour. The `normalize` is there because
interpolation across a triangle does not preserve unit length.

## Pitfalls checklist

Symptoms and their usual causes, in the order they tend to bite:

| Symptom | Cause |
| --- | --- |
| Black window, no error | Shader compiled but writes nothing to `FragColor`, or geometry is behind the camera / outside the near-far range |
| Cube visible on frame 1, gone afterwards | `GL_DEPTH_BUFFER_BIT` missing from `glClear` |
| Faces render through each other | `glEnable(GL_DEPTH_TEST)` missing, or no depth buffer was requested |
| Exception: uniform not found | Name typo, or the shader stopped reading that uniform and the driver stripped it |
| Cube distorted or invisible after a matrix change | `transpose` flag flipped, or C#/GLSL multiplication order no longer mirrored |
| Shader edits have no effect | Edited the copy under `bin/`, which the next build overwrites |
| `FileNotFoundException` on a shader at startup | A shader path was made relative again; it must resolve against `AppContext.BaseDirectory`, not the working directory |
| Nothing draws, no GL error | Element buffer unbound while the VAO was still bound during setup |
| Smooth gradients across cube edges | Vertices shared between faces instead of duplicated per face |
| NaN / cube never returns after minimising | Aspect ratio computed from a zero-height framebuffer |
| CS0104 on `Shader` | The `Silk.NET.OpenGL.Shader` collision; keep the alias in `Program.cs` |
