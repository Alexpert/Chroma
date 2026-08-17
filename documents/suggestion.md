# Suggestions

Everything the project has proposed and not built, by theme. Nothing here is a commitment, and
nothing here is ordered by priority inside its section.

**Only what is open is here.** An entry leaves this list the moment it is started or built, in
the iteration that takes it: what it settled is recorded in that iteration's section of
[roadmap.md](roadmap.md) rather than repeated here as background. So this list shrinks, and
reading it never means reading about something that already works.

The iteration sections of the roadmap keep their own **Next** paragraphs, because those belong to
the record of the iteration that wrote them. The items themselves are gathered here so that what
is open can be read as a list rather than found by rereading twenty iterations. What was taken
from this list and built is kept in [roadmap.md](roadmap.md#already-taken-from-the-suggestions).

An entry that is scheduled **moves** to [current_version.md](current_version.md), which is where
it is tracked while it is being built, and from there into the roadmap when it ships. Geometry
and primitives left this list that way, for 0.22.0.

## The language

**More mesh formats: glTF and PLY.** Iteration 24 read OBJ and STL, which are the two formats a
model is most often published as and the two that parse in an afternoon. Both of these are binary
with real structure, and glTF brings a scene graph and materials with it, which is a larger
question than a decoder: this renderer's materials are its own and a glTF's would have to be
mapped onto them rather than adopted. Worth taking when something needs it, not before.

**A height field from an image, once something decodes one.** Iteration 25 built `heightField`
and deliberately left the decoder out: `perlin` at bind time needs no I/O, and a scene whose
terrain is computed is reproducible from the file that describes it, which a scene whose terrain
is a `.png` beside it is not. The primitive is agnostic about where its grid comes from, so this
is one field on a node that already exists plus whatever decoder the **skybox** and **PBR
texture** entries end up needing. It is the third caller for that decoder and the cheapest of the
three, and it should not be the one that chooses the format.

**A min-max pyramid over a height field, for the grazing ray.** The march visits every cell the
ray crosses, so a ray nearly parallel to the ground at a thousand cells walks up to two thousand
of them, most far below it. A two-level pyramid of block extremes would cut that to a few hundred
block steps plus the cells that matter. What makes this a suggestion rather than a plan is that it
cannot buy **budget**: the march already costs the cost model a constant, so this is frame time
only, and it costs a second structure, a second packing and a second set of tie-breaks on the one
primitive whose selling point is that it needs none. Measure a grazing camera against an overhead
one first; if the ratio is small the pyramid is not worth its code.

**Rectangular height fields.** A `heightField` is square, which keeps `resolution` one number and
its diagnostics short. The block's header already has a spare lane for a second count, so this is
a second DDA limit and nothing else, and no format changes.

**UV coordinates, which have a reader waiting.** `ObjReader` already parses the `vt` line and
throws it away, because two floats per vertex uploaded for nothing is bandwidth spent on nothing.
The **PBR texture** entry further down spends its first point on the absence of UVs, and a mesh is
the one shape that has them, so the two features want each other and should be taken together.

**Noise as a material, which is not the `perlin` iteration 19 built.** The **Surface detail**
entry further down this list wants procedural noise evaluated *per hit in the shader*, through
the primitive's local space. That one is a texture; the built-in is a number in a scene file,
drawn before a shader exists. They would share a name, a formula and nothing else, and the
collision is worth recording here so that nobody expects a `perlin` in a `radius:` field to do
what a `perlin` in a material would.

**Basic objects on top of structs, as an evolution rather than a replacement.** This entry used
to read "instead of structs", to be taken *or* the structs entry; the records are built, so what
is left is the part they do not cover: **functions attached to a type**, and whatever comes
with them.

Deliberately not built, and worth keeping deliberate. Three things to weigh, and the first is
the one that decides it:

- **Records, functions and `include` already compose into most of what "basic OOP" means for a
  scene file.** `struct Post { … }` beside `function raise(p, by)` in the same fragment is a
  type and its operations, exported together and used together. The part that does not compose
  is *method call syntax*, `p.raise(0.5)` rather than `raise(p, 0.5)`, so the honest question
  is whether that syntax is the gain, or whether what is actually wanted is something else that
  has been called OOP by habit.
- **Every concept costs twice, once in the evaluator and once in the diagnostics.**
  Inheritance, dispatch and object identity are a large surface, and this language's diagnostics
  are half of what it is. A dispatch failure has to say something better than "no such method".
- **Identity collides with something already decided.** Referencing a binding twice
  instantiates it twice, and iteration 20 made structs and arrays immutable for the same
  reason: nothing here has a notion of *the same object* that survives being passed around.
  Objects with mutable state would introduce one, and it would then be the only kind of value
  in the language that has it.

If it is taken, the shape that fits what exists is a `struct` that may declare functions
alongside its fields, with `p.f(x)` resolving to the declared function with `p` bound to its
first parameter, with no inheritance, no dispatch and no identity. That is the smallest thing that
would add method syntax without adding a second value model, and it is what this entry means
by "basic".

**Arguments on the command line, readable from the scene.** `Chroma scene.chroma -D count=12`,
and the scene builds twelve of whatever it builds. It is the last piece of parameterisation
missing: iteration 8 sealed an included fragment from its host and said "parameterising one is what
macros are for", functions then did that inside a file, and nothing yet parameterises a scene from
outside it. `Chroma.SceneDump` takes the same flag or the two tools stop agreeing about what a
scene is.

- **Parse the value with the expression parser that already exists**, so `-D count=12` is a number,
  `-D tint=[1,0,0]` is a vector and `-D spline="bezier"` is a string. The alternative is that
  everything arrives as a string and the scene converts it, which needs conversion functions the
  language does not have.
- **A default is not optional.** Every check in this project runs the plain command: the manual's
  38 images, `build-manual.ps1 -Verify`, the gallery, and every byte-identical dump comparison. A
  scene that cannot load without arguments breaks all of them, so the reading form carries its own
  fallback and a missing argument is a diagnostic naming the argument rather than a crash.
- **It has the seed's problem.** A scene whose image depends on the command line is no longer
  reproducible from the file alone, which is the property the `random` entry above spends its first
  point defending. The honest position is that the file *and its arguments* are the scene, and that
  nothing under `scenes/manual/` may take any, or `-Check` stops meaning anything.
- **The no-shadowing rule decides where they land.** Nothing shadows here, so an argument arriving
  as an outermost binding makes a `let` of the same name an error in a file that has no way to see
  it coming. That is an argument for an accessor with a default rather than a pre-declared name.

## Light transport and appearance

**A skybox.** Half of it is already built, and knowing which half is what makes this entry
tractable. `BACKGROUND` is a black constant in the shader, and a ray that escapes adds it to the
path's radiance like any other emitter: the environment has been a *uniform light* since iteration
4, not a backdrop drawn behind the geometry. What a skybox adds is direction dependence and a
source of colour. There is no new mechanism underneath it.

1. **Three tiers, and they are not one feature.** A constant colour is a `render { }` field beside
   `maxBounces` and `exposure`, costs nothing, and would retire the false alarm iteration 6
   recorded: a face lit by nothing reads as broken geometry, and it is the black environment rather
   than the shape that makes it so. A procedural sky, ground and horizon gradient still needs no
   data and gives a scene a direction to be lit from. An image-based environment map is the real
   one, and it needs the same decoder the **height field from an image** entry needs, plus an HDR
   format: an 8-bit sky clipped at 1.0 cannot light the scene it is meant to be lighting.
2. **The cost is in the sampling, not the display.** Showing a sky is trivial. A *bright* sky is a
   light that paths find only by chance, which is precisely the limitation iteration 4 accepted for
   emissive solids and named as the reason multiple importance sampling was unnecessary here.
   Sampling an environment map means an importance distribution over its luminance, and building
   one un-retires MIS, because a path would then reach the sky two ways. That is the same door
   iteration 9's item 3 opens for emissive solids, so the two are one question and should be priced
   together rather than twice.
3. **The default has to stay black.** A non-black environment changes every image in this
   repository, and the manual's `-Check` compares 38 of them byte for byte. That is the test of
   whether the feature was added or the renderer was changed, and it is the same measurement every
   language revision here has had to pass.
4. **Shadow and transmittance rays need nothing.** They ask whether something is in the way and
   never what is behind it, so they miss the environment by construction and stay as they are.

**A library of measured materials.** A `.chroma` fragment of named materials shipped with the
renderer and included by a scene, sourced from [physicallybased.info](https://physicallybased.info/)
and its roughly 140 entries across metals, liquids, organics and manufactured surfaces.
`scenes/manual/palette.chroma` already exists as a fragment with no camera, so the shape of the
thing is settled and this is its useful version. It is also the first real user of `include` as a
module, and it will meet the flat namespace named above on its first collision: a scene that
defines `gold` and includes a library that defines `gold` is an error today.

Four things stand between the site and a file, and none of them is typing.

- **Colour space.** The site lets the reader pick sRGB or linear sRGB and does not say which it
  defaults to. `color` here is linear. Pasting an sRGB triple is a gamma error, and a gamma error
  on a base colour reads as a lighting bug rather than as a wrong number.
- **Metals do not map one to one.** The site carries complex IOR, specular colour and an F82 term
  for conductors. This renderer has `metallic` and a base colour that becomes F0, so the useful
  column is reflectance at normal incidence and the F82 term has nowhere to go. A copper will be
  close and will not be exact, and the library should say so per entry rather than imply a
  measurement it does not reproduce.
- **`density` is not a field here**, and the liquids are where that bites: `absorption` and
  `scattering` are per world unit, so a wine or a skin needs a conversion that depends on the
  thickness the scene intends. That is a derivation, not a copy, and it is the part most likely to
  be got quietly wrong.
- **The site states no licence.** Measured constants are not much of a copyright question, but a
  curated file of 140 entries shipped inside a release archive deserves an attribution line and a
  look at the terms before it ships rather than after.

**Done when** a scene can `include` the library and name a material, and the manual has a rendered
chart of the whole set, which is also the test that every entry still loads.

**A lens, and the depth of field that comes with it.** The camera is a pinhole: `position`,
`lookAt`, `up` and `fov`, and everything is in focus at every distance. PBRT chapter 5.2 is the
whole recipe and it is two fields and a few lines of shader: sample a point on a disk of radius
`aperture`, aim the ray through the point the pinhole ray reaches at `focalDistance`, and let the
accumulation buffer average the rest. At `aperture: 0` it is exactly the renderer of today, so it
costs nothing until it is asked for. Listed because it is the cheapest thing in this document that
changes how a render *looks* rather than what it costs.

**A reconstruction filter.** The primary ray is already jittered inside its pixel, and every sample
is then averaged with equal weight, which is a box filter, which is the filter with the worst
properties of any in use. PBRT chapter 8.8 is the reference. A Gaussian or Mitchell filter needs
each sample weighted by where it landed, so the running mean grows a weight channel, which is the
same accumulation-buffer change adaptive sampling needs and is a reason to do the two together. It
changes every image, so it arrives the way a non-black environment does: behind a default that
reproduces what exists.

**Spectral rendering, and the prism that would prove it.** Three channels is a choice this renderer
has never revisited, and PBRT 4 changed its own default to sampled wavelengths (chapter 4.5, with
colour handling in 4.6). Dispersion is listed under the named limits above with no route; this is
the route, and it is a large one, since every radiance value would carry wavelengths and every
material table would become spectra rather than RGB triples.

The deliverable is a prism throwing a rainbow onto a wall, in the manner of every deliverable in
this document, and the geometry is already free: `prism` takes a three-point contour. It is the
right test because it is the picture an RGB renderer cannot fake, and because it fails
informatively. Six things it forces:

1. **`ior` becomes a curve.** One number per material becomes a dispersion model: Cauchy's two
   coefficients, Sellmeier's six, or an Abbe number beside the `ior` already there. That is the
   only language-visible change, and it defaults to no dispersion so that every existing scene is
   untouched.
2. **Three samples give three bands, not a spectrum.** Dispersion computed in RGB produces a red, a
   green and a blue fringe, which is a known wrong picture, and that is exactly why this scene is
   the test that forces real wavelength sampling instead of an approximation that looks close on
   everything else.
3. **One wavelength per path is colour noise.** Hero wavelength sampling, four correlated
   wavelengths carried together, is the standard answer, and it is what keeps the rainbow from
   arriving as confetti.
4. **The light needs a spectrum.** White is not a colour. The band hues are right only if the
   source has a defined spectral power distribution, D65 or equal energy; a light whose spectrum is
   `[1, 1, 1]` makes a rainbow of the wrong colours.
5. **The output path grows a conversion**, spectral radiance to XYZ through the CIE curves and then
   to sRGB, ahead of the exposure and ACES pass that already exists.
6. **It is a caustic, so it is the slowest scene here by construction.** `glass.chroma` needs
   20 000 samples because a specular path to a small source is found by chance, and a rainbow is
   that with a narrow beam and a wavelength attached. Budget for it rather than be surprised by it.

**Verified how**, since "it looks like a rainbow" is not a measurement: a prism's deviation angle
at a given wavelength is analytic, so where red and violet land on the wall is a prediction before
it is a render. The check is that the bands sit at the predicted angles in the predicted order,
not that the image is colourful.

**PBR texture sets from the web, with normal and displacement maps.** A material is a handful of
numbers today; a downloaded set is six images, base colour, normal, roughness, metallic, ambient
occlusion and height. Reading six images is the easy part. This renderer has no texture
coordinates, no image decoder and no ray differentials, and one of the six changes the geometry.

- **There are no UVs, and in general there cannot be.** A CSG solid is not a parameterised surface,
  which is the same fact that stops an emissive solid being sampled. Two answers, and the entry has
  to pick one. Triplanar projection needs no parameterisation at all: three projections blended by
  the normal, in the primitive's local space, which the baked inverse matrix already provides. A
  per-kind parameterisation, spherical on a sphere and face-based on a box, is exact where it
  applies and undefined the moment a `difference` cuts a new face through it. Triplanar is the one
  that survives CSG, and it is the one that also solves the next point.
- **A normal map needs a tangent frame**, and no surface here carries one. Triplanar gives one per
  projection axis by construction.
- **The decoder is owed three times.** This entry, the skybox and the height field all have to
  read an image, and `PngWriter` only writes. Choosing the format and the library once, for all
  three, is cheaper than answering it three ways, and it is the first dependency this project would
  take on for a reason other than windowing.
- **Displacement is the one that cannot be faked here and cannot be done here.** The geometry is
  exact analytic intervals, and displacing a surface by a texture makes the span boundaries wrong,
  which is what everything downstream rests on. Three honest options: use the height map as a bump
  only, which changes shading and never the silhouette; march the displaced surface *inside* the
  span the primitive already produced, which is relief mapping, is bounded, and is the same kind of
  march `heightField` already runs; or feed the image to `heightField` itself and get real geometry
  with a real silhouette, at the price of it being a primitive rather than a material. Iteration 25
  built that primitive, so the third option is now one decoder away.
- **Filtering, or it will shimmer.** A 4K texture minified with no mip-mapping is the classic
  crawling image, and choosing a mip level needs ray differentials, which this renderer has never
  had and has never needed. PBRT chapter 10.1 is the treatment. This is the item most likely to be
  skipped and then blamed on the sampler.
- **Weight and licence.** One set is tens of megabytes against a repository that is text plus 5.9 MB
  of manual images, and the release archives are self-contained. Only CC0 sources can ship inside
  them; the alternative is that a scene names a path the reader supplies, which makes the scene
  unreproducible and is a real cost rather than a detail.

This is the file-fed half of **Surface detail** below, which is the procedural half. They share the
coordinate question and nothing else, and the coordinate question is the one worth answering first.

**Surface detail.** Procedural patterns — POV-Ray's pigments and normals: checker, gradient,
noise — mapped through the primitive's *local* space, which the baked inverse matrix already
provides at no cost. Normal perturbation for bumps. Both are material-side and touch no
geometry.

**Heterogeneous media.** Split out of iteration 10 for the same reason: a density field, whether
procedural noise or a 3D texture, plus delta or ratio tracking to sample free flight through it.
Nothing in iteration 10 needs to be built differently to make this reachable.

**The named limits.** [transparency.md](transparency.md#limits-of-this-implementation) lists
what the renderer cannot do — nested media, dispersion, subsurface scattering, shadow rays that
do not refract. None of them is scheduled. Iteration 9 was to price them and is on standby, so
anything taken from this list before it runs is taken on intuition — which is a reason to say so
out loud, not a reason to avoid it.

## The compiler, and speed

**The cost model's weights are wrong between shape kinds, by about 3x.** Iteration 15 measured that
and said so rather than fitting a number to it; iterations 17 and 18 both close with "still".
`ShapeCost.Budget` is a placeholder until it is fixed, and the budget is what the chunker and the
cutter both decide on, so every number they produce inherits the error. The calibration sweep is
`tools/measure-shape-cost.ps1`, and the first thing to fix is that its own base was most of what it
was measuring.

**Compaction in the wavefront**, which is where the rest of its speed is. Every stage dispatches at
full resolution today, alive ray or not.

**Nobody has measured which shape of `cube.chroma` renders faster.** Cutting stops as soon as the
width rule is satisfied, so the scene ends as four hundred appearances of a twenty-leaf shape
rather than eight thousand of a one-leaf box. Iteration 17 opened the question and iteration 18
repeated it unanswered.

**The loader re-probes from scratch on every cut round**, and the first round probes a tree it
already knows it is about to cut apart. Iteration 18 named it as the next thing, if a scene bigger
than `cube.chroma` is ever wanted.

**A mesh is loaded, welded and hierarchised once per node and again per probe.** Iteration 24
left this and it is the one number it made worse: the test suite went from 3 seconds to 2 minutes
11, because `scenes/meshes.chroma` names the bunny four times and every probe walks it again.
Nothing about the picture is wrong and nothing about the design has to change: the packed block
is deliberately position-independent, so it can be cached by the signature that already exists,
and the decoded mesh can be cached on the resolved path. Two dictionaries. The reason it was not
done in the iteration is that it is a speed fix wearing a correctness fix's clothes, and the
iteration's own claim — that a mesh costs the *program* nothing — is about the shader rather than
about the loader.

Iteration 25 made the entry both wider and cheaper to answer. A height field's expensive half, the
million calls that fill its grid, happens in the **binder** and lives on the solid, so no probe
repeats it; only the packing repeats, and that is a copy. But the packing is a million texels, and
`SceneTables.BlockOffsets` interns by signature within one emitter and a probe builds a fresh one.
The third dictionary is the same two lines. There is also a shortcut worth considering first: a
probe exists to compare *emitted text*, and inside one every block starts at offset zero anyway,
so a probing emitter could skip the payload entirely. That is safe only because the signature is
what distinguishes two blocks, which is exactly what iteration 24 had to add and 25 reused, and it
wants a test with two different fields in one root before anyone relies on it.

**Adaptive sampling**, planned in iteration 11 and not built. The per-pixel error is already
computed, so samples can go where the error is, and the estimator stays unbiased only if the
per-pixel sample count is carried into the average. The accumulation buffer has nowhere to put one:
RGB is the running mean and alpha the running mean of the squared luminance, which the convergence
meter needs. It wants a second render target and a change to the buffer's layout, and it should be
measured against the current baseline rather than the one it was planned against.

**SPIR-V**, cheap to try, and worth a little less with every iteration that moves the ceiling by
other means.

## Tooling and workflow

**Workflow.** Hot-reload of the scene file on a `FileSystemWatcher` — the parse-to-upload
path is fast and stateless, so this is nearly free and changes how the tool feels to use.
Orbit camera on the mouse.

**~~A VS Code extension~~: built, less completion.** `editors/vscode` is the extension and
`tools/pack-vscode.ps1` packs it, into the `chroma-<version>.vsix` a release now attaches beside
the four archives. Both halves came in at the size this entry predicted: a TextMate grammar of
about a hundred lines, and one file of dependency-free JavaScript that spawns a process.

- **The grammar's word lists are no longer a copy anyone has to remember.** The prediction was
  that they would drift, the keyword list having grown twice already. `GrammarTests` reads the
  three lists back out of the JSON and compares them to `Lexer.ReservedWords`, `Builtins.Names`
  and `NodeBinderRegistry.Names`, so a keyword added without being coloured fails `dotnet test`
  naming the word that was added. It cost two extractions of lists that were already there, and
  no new machinery. Everything else in the grammar is a rule rather than a list: a field is any
  name written before a colon, which colours the fields of a node type added tomorrow.
- **Diagnostics turned out not to be a problem matcher.** That is a task somebody has to run.
  Publishing them from the extension instead is the same quantity of code and gives the Problems
  panel, a squiggle under the word, and an error inside an imported fragment reported in the
  fragment. It is still not a language server and still reimplements nothing: it spawns
  `Chroma.SceneDump` on open and on save and reads back the `path:line:column: severity: message`
  lines the loader has always printed. That format being the conventional one is what made the
  reading three lines.
- **It will not check as you type**, which is the one thing the shape costs. The tool reads the
  file from disk, and a dirty buffer written to a temporary file elsewhere would break `import`,
  whose paths resolve against the importing file.
- **Completion is still open and still owes exactly what this entry said it owed**: the node
  types and their fields, generated from `NodeBinderRegistry` as part of the build rather than
  copied into an editor. Nothing built here made that any cheaper.

**Packing a `.vsix` needs no Node toolchain**, which was worth establishing before the extension
was allowed to exist. It is a zip in the OPC layout, so `[Content_Types].xml`, a `.vsixmanifest`
and entry names written with forward slashes are the whole of what `vsce` would have contributed.
The extension declares no dependency and is loaded exactly as it is written, so there is nothing
to build either: the repository gained a deliverable and no second toolchain.

## Testing and measurement

**Testing.** The front end is covered; the renderer is not, and cannot be by the same
means. A CPU reference implementation of the span algorithm, as another `ISolidVisitor`,
would fix that: the algorithm is already specified independently of GLSL, and having it in
C# turns "the picture looks wrong" into an assertable unit test. It is worth more now than
when it was written, since iteration 9 will need a trusted reference whenever it runs, and a
second renderer is a much heavier way to obtain one.

**PBRT 4 is the reference text, and this is what it already answers.** Iteration 9 names pbrt v4 as
the renderer to compare against; the book itself settles several of the questions left open above,
and the map is worth keeping so that none of them is researched twice.

| Open question, from above | Where it is answered |
| --- | --- |
| Sampling an emissive CSG solid, the largest limitation here | Appendix A.2, reservoir sampling, which is the RIS machinery iteration 9 parked |
| A bright sky that lights the scene | 12.5 infinite area lights and 12.6 light sampling, with the 2D distribution built by the alias method of A.1 |
| Multiple importance sampling, once either of those lands | 13.4, "a better path tracer" |
| Heterogeneous media | 14.2, null scattering and ratio tracking, which is the modern form of the delta tracking that entry names |
| Compaction in the wavefront | 15.1 and 15.2, where the queues and their compaction are the subject |
| Whether a better sampler is worth anything here | 8.5 to 8.7, against iteration 11's measured 0.1%, which is the result to explain rather than repeat |
| A reconstruction filter, a lens, rounding error | 8.8, 5.2 and 6.8, as the three entries above say |

Two things it does not answer, and they are the two this renderer is built on: pbrt has no CSG, and
generates no code. The interval algorithm, the per-scene shader and everything iterations 12 to 18
did about the instruction ceiling stay this project's own problem, and stay the part worth writing
up rather than reading up.

**Iteration 9 is on standby, and one open question is parked with it.** The comparison against a
reference renderer has never been run. The question it took with it is the largest limitation this
renderer has: whether resampled importance sampling retires "a CSG solid cannot be sampled
uniformly", which would make emissive solids reachable by next-event estimation and un-retire the
multiple importance sampling iteration 4 shelved. The skybox entry above needs the same machinery
for a bright sky, so the two are one question and should be priced together.

