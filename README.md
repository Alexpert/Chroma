# ChromaTest

A minimal Silk.NET / OpenGL 3.3 Core starting point: running the project opens a window
and draws a cube through a GLSL shader pair you can edit.

The default shader colours each face by its normal, so the cube shows three distinct
faces — immediate visual confirmation that your shader is the one running.

## Requirements

- .NET 8 SDK
- A GPU driver exposing OpenGL 3.3 Core (anything from the last decade)

## Running

```sh
dotnet run
```

A 1280x720 window titled `ChromaTest` opens with the cube centred. Press `Escape` to close.

## Editing the shader

The two files you are meant to touch are:

- [`Shaders/cube.vert`](Shaders/cube.vert) — vertex stage
- [`Shaders/cube.frag`](Shaders/cube.frag) — fragment stage, the main editing surface

They are plain text, compiled at startup, and copied next to the binary on build
(`PreserveNewest`). Edit a file and re-run `dotnet run` — no other step is needed.

Available uniforms: `uModel`, `uView`, `uProjection` (all `mat4`).
Vertex inputs: `aPosition` at location 0, `aNormal` at location 1.

If a shader fails to compile, the process exits with the driver's compilation log printed
to the console, including the line number.

> Note: a uniform your shader never reads is stripped by the driver, and setting it from
> C# then throws. Remove the matching `SetUniform` call in [`Program.cs`](Program.cs) if
> you drop a uniform from the shader.

## Documentation

- [`documents/architecture.md`](documents/architecture.md) — structure, lifecycle, data flow
- [`documents/implementation.md`](documents/implementation.md) — per-file walkthrough and pitfalls

## Possible next steps

None of these are implemented — they are the obvious directions from here:

- **Shader hot-reload.** A `FileSystemWatcher` on `Shaders/` that recompiles on save, so
  you never restart the app. Keep the previously linked program bound when compilation
  fails, and print the log instead of throwing.
- **Continuous rotation.** Accumulate the `deltaTime` already passed to `OnRender` and
  rebuild the model matrix each frame, to see the shader across all faces.
- **Orbit camera.** `Silk.NET.Input` is already referenced: drag to rotate around the
  cube, wheel to zoom, by driving the view matrix from mouse state.
- **ShaderToy-style uniforms.** Feed `uTime` and `uResolution` so animated effects can be
  written entirely in GLSL with no C# change.
- **Textures.** `StbImageSharp` to decode, plus a `Texture` class mirroring `Shader`, and
  a UV attribute in the vertex layout.
- **Real lighting.** The normals are already there; a light direction uniform and a
  Lambert/Blinn-Phong term in the fragment shader is a short step.
