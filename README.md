# Chroma

A GPU ray tracer for **CSG**, Constructive Solid Geometry. You describe a scene in a text file,
pass the file to the program, and it renders the solids by tracing rays against them in a shader.

CSG builds shapes by combining simpler ones with boolean operators: a bolt is a cylinder *union*
a hex head *minus* a threaded groove. Rather than triangulating that, the renderer intersects
rays with the boolean expression directly, so the surfaces are exact at any distance and there is
no mesh anywhere in the pipeline.

| | | |
| --- | --- | --- |
| ![A Cornell box with a metal sphere](documents/images/gallery/cornell.png) | ![Glass spheres over a caustic](documents/images/gallery/glass.png) | ![A shaft of light through haze](documents/images/gallery/fog.png) |

It is a path tracer. Light bounces, so a red wall tints the white floor beside it, metals reflect
their surroundings and shadows have real penumbrae. Glass refracts what is behind it, tints with
its own thickness and throws a caustic. A solid can also hold fog or smoke that light scatters
inside rather than merely crosses, so a beam through haze is visible from the side.

More pictures in the [gallery](documents/gallery.md), and how to write one of these from scratch
in the [illustrated manual](documents/manual.md).

```js
// A box with a spherical bite taken out of it: the case that cannot be rendered correctly
// without exact CSG, because the visible surface inside the cavity is the far side of the
// sphere with its normal reversed. It is the left half of scenes/csg.chroma.

camera { position: [0, 2, 6], lookAt: [0, 0, 0], fov: 45 }

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
Chroma scenes/csg.chroma
```

## Download

[**Get the latest release**](https://github.com/Alexpert/Chroma/releases/latest): one archive per
platform, each carrying both programs, the shaders, the sample scenes, the illustrated manual and
the .NET runtime itself. Nothing to install and nothing to build: unzip and run. The only
requirement is a GPU driver exposing **OpenGL 3.3 core** or newer.

| Platform | Archive | How to start it |
| --- | --- | --- |
| Windows x64 | `.zip` | `.\Chroma.exe scenes\cornell.chroma` |
| Linux x64 | `.tar.gz` | `chmod +x Chroma Chroma.SceneDump`, then `./Chroma scenes/cornell.chroma` |
| macOS, Intel and Apple silicon | `.tar.gz` | the same, plus the two steps [below](#macos-needs-the-binaries-signed) |

`RUNNING.txt` inside each archive repeats the platform's own steps. Every `Chroma …` command in
this README is `dotnet run --project src/Chroma -- …` from a clone.

**Write the `.\` on Windows.** PowerShell never searches the current directory, so a bare
`Chroma.exe` fails there whatever the folder; cmd.exe accepts the bare name but rejects
`./Chroma.exe`. `.\Chroma.exe` is the one form both shells run.

#### macOS needs the binaries signed

The archives are cross-published from Windows, so their binaries carry **no code signature**, and
macOS kills an unsigned binary at launch on Apple silicon: `killed`, with no explanation. Signing
them ad-hoc, on the Mac, is the workaround. It needs the Xcode command line tools
(`xcode-select --install`):

```sh
chmod +x Chroma Chroma.SceneDump
xattr -dr com.apple.quarantine .
find . -type f \( -name "*.dylib" -o -name "Chroma" -o -name "Chroma.SceneDump" \) \
  -exec codesign --force --sign - {} \;
```

Publishing the macOS archive on a Mac is the real fix, and a later release will do it.

> Windows is the only archive that has been run end to end. macOS has not been seen past the
> signature check above, and Linux is unlaunched. Reports welcome.

## Rendering

```sh
$ Chroma scenes/cornell.chroma
cornell.chroma: 8 primitives, 8 shapes, 6 materials, 1 lights, 406 generated lines,
widest root 1 spans, estimated 312 statements (1% of the instruction budget); lean shader
OpenGL 4.6 on NVIDIA GeForce RTX 4070 SUPER -- fragment shader, texture buffers
```

The first line says what the scene holds and what it was compiled into, the second what it is
being traced by. A 1280x720 window then opens on the scene, and `Escape` closes it. Everything in
the file, from the camera position to the materials and the transforms, takes effect on the next
run, with no rebuild.

**The image arrives noisy and cleans itself up.** One light path per pixel is traced per frame
and averaged into everything before it, so a still camera converges over a few seconds rather
than presenting a finished picture immediately. Resizing the window starts it over.

To end a run by itself and keep the picture:

| Option | What it does |
| --- | --- |
| `--samples <n>` | stop after n samples per pixel, write a PNG to `renders/`, close |
| `--error <percent>` | stop at a noise level instead of a sample count |
| `--output <path>` | write the PNG exactly there |
| `--size <w>x<h>` | ask for a framebuffer other than 1280x720 |
| `--headless` | never show the window |
| `--no-update-check` | do not ask GitHub whether a newer release exists |

```sh
Chroma scenes/fog.chroma --samples 400
Chroma scenes/cornell.chroma --error 5
```

`--output` and `--headless` need a run that ends by itself, so they go with `--samples` or
`--error`. The sampler is seeded from the pixel and the frame index, so the same scene at the
same size and sample count gives the **same PNG byte for byte**, which is what lets every
illustration in the manual be rebuilt and compared. The manual's table lists the rest of the
options, which are levers for comparing one rendering path against another rather than things a
picture needs.

**The one thing it sends over the network.** An interactive run asks GitHub once a day whether a
newer release exists, and says so with a link if it does. It only ever detects: nothing is
downloaded and nothing is replaced. The request carries nothing but the version asking, it cannot
delay or fail a render, and a run given `--samples`, `--error`, `--headless` or `--output` never
makes it at all. `--no-update-check` refuses it outright.

## Inspecting a scene

`Chroma.SceneDump` prints the hierarchy the parser understood. When a picture is wrong, this is
what tells you whether the file was read the way you meant.

```sh
$ Chroma.SceneDump scenes/csg.chroma
Camera   position <0, 2, 6>  lookAt <0, 0, 0>  up <0, 1, 0>  fov 45
Render   maxBounces 4  exposure 1.3  seed 0  angles degrees

Lights
  +- PointLight        position <2, 4, 3>  color <1, 1, 1>  intensity 55  radius 0.4
  `- DirectionalLight  direction <-0.57735, -0.57735, -0.57735>  color <0.8, 0.8, 1.1>  intensity 1

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

Mistakes are collected and reported together, with a line and a column, rather than one per run:

```sh
$ Chroma.SceneDump scenes/diagnostics-demo.chroma
scenes/diagnostics-demo.chroma:8:5: error: 'radius' is already defined
scenes/diagnostics-demo.chroma:20:3: error: unknown field 'raduis' on 'sphere'
scenes/diagnostics-demo.chroma:24:8: error: field 'min' expects a vector of 3 components, found a vector of 2 components
scenes/diagnostics-demo.chroma:28:1: error: 'difference' needs at least 2 operands, found 1
4 errors; scene not loaded.
```

## The scene language

A scene file is a tree of blocks. A block is a **type name followed by an object literal**, and
inside it `name: value` is a field while a bare block is a child. `//` and `/* */` comment,
`[x, y, z]` is a vector, and arithmetic works on vectors component by component.

Twelve primitives are available: `sphere`, `box`, `cylinder`, `cone`, `plane`, `torus`, `prism`,
`lathe`, `blob`, `sphereSweep`, `quadric` and `mesh`. Every one of them is a solid with an inside,
so every one is a legal operand of `union`, `intersection` and `difference`. A `prism`, a `lathe`
and a `sphereSweep` all take cubic Bézier curves; a prism or a lathe may hold several contours, so
a hole is part of the outline rather than a `difference`. `mesh` loads an `.obj` or `.stl` file
and checks that it is closed before accepting it, because a pile of triangles has no inside;
`close: true` fills the holes of a file that is nearly one. Beside them are `camera`, `pointLight`,
`directionalLight`, `material`, `object` and `render`. Every field of every one is listed in
[documents/scene-language.md](documents/scene-language.md), with what it takes and what it means.

**Scenes are described, but a description repeated a hundred times is worth writing once.** The
control flow is JavaScript's, down to the braces: `for (let i = 0; i < n; i++)`, `if`/`else`, and
`condition ? a : b` where a *value* has to be chosen. `if` and `for` may appear anywhere a field
or a child may. `scenes/lattice.chroma` builds 125 cells and 425 solids in twenty-five lines:

```js
for (let x = 0; x < n; x++) {
  for (let y = 0; y < n; y++) {
    for (let z = 0; z < n; z++) {
      union {
        let p      = ([x, y, z] - mid) * step;
        let corner = (x == 0 || x == n - 1) && (y == 0 || y == n - 1) && (z == 0 || z == n - 1);

        sphere { center: p, radius: node }

        if (x < n - 1) { cylinder { base: p, cap: p + [step, 0, 0], radius: strut } }

        material: corner ? gold : steel
      }
    }
  }
}
```

**A shape worth repeating with a difference is a function.** `function` is a `let` that takes
arguments, and `object` places a binding without pretending to be a boolean operator. A function's
body is evaluated where it was *declared*, not where it is called, so a file of `function`
declarations can be `import`ed and used without knowing what the scene around it names.

```js
function column(i) {
  return union {
    drum(0, 0.42, 0.22)
    drum(0.22, 0.3, height - 0.46)

    translate: [(i - 2) * spacing, 0, 0]
    material: stone(i * 2 == count - 1 ? warm : grey)
  };
}

for (let i = 0; i < count; i++) { column(i) }

object { lintel, translate: [0, height, -0.9] }
```

**Values.** Numbers, booleans, strings, vectors, whole nodes, and two containers: `[ ... ]` holds
anything and nests, and `struct` declares a record type in the C sense, a fixed set of named
fields checked where an instance is written. An array of numbers *is* the language's vector rather
than a second kind beside it, so `normalize([1, 1, 0]) * 3 + [0, 4, 0]` means what it looks like.
An array written as a child contributes its elements, so `union { shapes }` places all of them.
Assignment rebuilds the container and rebinds the name rather than changing anything in place, so
`let q = p;` neither copies nor shares.

The operator table is C's, whole, at C's precedence, and `&`, `|` and `^` carry both of C's
readings: two booleans give the logical connective, two whole numbers the bitwise one. The
built-in library runs from `sin` to `clamp`, with `length`, `normalize`, `dot` and `cross` for
vectors, and `PI`. Angular fields are degrees unless the scene says `render { angles: "radians" }`
once.

**`random` and `perlin` make a hundred identical posts differ.** The numbers are drawn while the
scene is being built, on the CPU, before anything is compiled, and `random(i)` takes an argument
rather than being a stream, so no result depends on the order the evaluator walks the tree. The
seed is written in the file, so the same file gives the same image on another machine.

**`import` reuses a file**, either into the current scope or behind a name, and `private` in front
of a `let`, a `function` or a `struct` keeps it inside the file that declared it:

```js
import "palette.chroma";                  // its exports land here
import "warm.chroma" as warm;             // or behind a name, so two files may both say 'gold'

sphere { material: warm.gold }
```

Control flow runs in the evaluator rather than in a preprocessor ahead of the lexer, which is what
keeps every diagnostic pointing at a line and column **in the file you wrote**, inside a loop body
and inside an imported file alike.

## Editing scenes in VS Code

[`editors/vscode`](editors/vscode) is an extension for `.chroma` files, attached to every release
as `chroma-<version>.vsix` and built from a clone with
`powershell -File tools/pack-vscode.ps1 -Install`.

It does two things. It **colours** a scene: the reserved words, the node types, the built-in
functions, the fields and the literals, from a grammar that a test keeps equal to the lexer's own
lists. And it puts the **diagnostics above into the Problems panel**, by running
`Chroma.SceneDump` when a scene is opened or saved, so an error in the editor is the same sentence
as an error in the terminal, on the same line and column.

Highlighting needs nothing installed. Checking needs the executable: `chroma.sceneDumpPath` names
it, and when that setting is empty the extension looks under `src/Chroma.SceneDump/bin` in a
clone, beside an unzipped archive, and on `PATH`.

## How it works

The split between CPU and GPU is the design's centre of gravity. POV-Ray, the obvious point of
comparison, parses and traces on the CPU. Here the CPU parses and *compiles*, and the GPU traces.

```
.chroma file  ->  lex / parse / bind  ->  scene tree  ->  emit GLSL  ->  compile
                                                                            |
                one fullscreen quad, fragment shader traces every pixel  <--+
```

- **Exact intervals, not distance fields.** A primitive does not answer "where is your nearest
  surface". It returns every *span* of the ray that lies inside it, and the operators merge those
  span lists. That is what makes `difference` produce a genuinely correct cavity with correctly
  flipped normals.
- **A span knows which side of a surface you are on.** `[tIn, tOut]` says whether a ray is
  entering a solid or leaving it, which is what refraction needs and where the thickness of glass,
  and therefore its colour, comes from.
- **A shader generated for the scene.** The scene tree becomes GLSL for those solids and no
  others, so nothing is sized for the worst scene anyone might write. Only the geometry is
  generated: the path tracer around it stays a hand-written file you can read.
- **Repeats are free.** What a scene costs is how much *different* geometry it holds, not how
  much. The compiler works out which roots are the same solid standing somewhere else, emits one
  of them, and puts the rest in a buffer with a tree over them. A scene past what one program will
  take is split into chunks and traced a pass at a time, on its own, with nothing to say in the
  file or on the command line.

## Documentation

To write a scene, and shipped inside every release archive:

- [documents/manual.md](documents/manual.md): **start here.** Every feature in the order you meet
  it, with a rendered picture beside each example
- [documents/gallery.md](documents/gallery.md): the sample scenes, rendered, one paragraph each
- [documents/scene-language.md](documents/scene-language.md): the reference for the `.chroma`
  format. Every node, field and function, with what it takes and what it gives back

To change the renderer:

- [documents/architecture.md](documents/architecture.md): the stages and where the boundaries sit
- [documents/csg-raytracing.md](documents/csg-raytracing.md): the interval algorithm and the GPU
  encoding
- [documents/lighting.md](documents/lighting.md): the rendering equation and the BRDF
- [documents/transparency.md](documents/transparency.md): refraction, absorption, caustics, media
- [documents/code-generation.md](documents/code-generation.md): why each scene becomes its own
  shader
- [documents/gpu-backends.md](documents/gpu-backends.md): how large a scene a driver will compile
- [documents/instancing.md](documents/instancing.md): finding the same solid standing elsewhere
- [documents/cutting-unions.md](documents/cutting-unions.md): a shape too large for any program,
  cut into the operands of its own `union`
- [documents/raymarching.md](documents/raymarching.md): distance fields, measured against exact
  intervals
- [documents/csg-tree-optimization.md](documents/csg-tree-optimization.md): whether the CSG tree
  is worth rewriting first
- [documents/performance.md](documents/performance.md): what a frame costs and where it goes
- [documents/implementation.md](documents/implementation.md): per-file notes and a
  symptom-to-cause pitfalls table
- [documents/roadmap.md](documents/roadmap.md): what each iteration delivered
- [documents/suggestion.md](documents/suggestion.md): what is proposed and not built
- [documents/current_version.md](documents/current_version.md): what the next release contains
- [documents/documentation-rules.md](documents/documentation-rules.md): how these documents are
  written

## Building from source

.NET 8 SDK, and the same OpenGL 3.3 driver.

```sh
dotnet build Chroma.sln
dotnet test
```

## Iterations

| Iteration | Deliverable | State |
| --- | --- | --- |
| 0 | Design and reference documentation | done |
| 1 | Scene parsing and the hierarchy dump tool | done |
| 2 | First render: camera, lights, sphere / box / cylinder | done |
| 3 | CSG operators: union, intersection, difference | done |
| 4 | Correct lighting: bounces, PBR materials, soft shadows | done |
| 5 | Transparency, refraction, Fresnel, caustics | done |
| 6 | Six more primitives: cone, plane, torus, prism, lathe, blob | done |
| 7 | `sphereSweep`, Bézier lathes, string literals | done |
| 8 | Language revision: conditions, loops, `import` | done |
| 9 | Measured against the state of the art | standby |
| 10 | Participating media: scattering, fog, smoke | done |
| 11 | Speed, at equal image | done |
| 12 | Per-scene code generation | done |
| 13 | The illustrated manual | done |
| 14 | Instancing: the same solid, placed many times | done |
| 15 | A cost model for what a scene compiles into | done |
| 16 | Feedback while the first image is compiling | done |
| 17 | Cutting inside a top-level `union` | done |
| 18 | The loader stops counting | done |
| 19 | Randomness, and the rest of C's operators | done |
| 20 | Arrays, structs, and vector maths | done |
| 21 | Documentation rules, and the manual in the archive | done |
| 22 | The geometry the existing primitives were missing | done |
| 23 | Rounding error, as a subject rather than a constant | done |
| 24 | Meshes: `.obj` and `.stl` as CSG solids | done |

Iteration 9, an audit against the state of the art, is on standby rather than skipped.
[documents/roadmap.md](documents/roadmap.md) says what each one settled and why;
[documents/current_version.md](documents/current_version.md) says what the next release will
carry.
