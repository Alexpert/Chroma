# Height fields

How a grid of numbers becomes a solid: where the samples come from, what the block in the shape
buffer holds, and why the march that traces it costs the program nothing. The tracing itself is in
[csg-raytracing.md](csg-raytracing.md#height-field-a-march-over-known-data), because it is one
more entry in the list of primitives and belongs beside the others.

Built in iteration 25. The user-facing half is in
[scene-primitives.md](scene-primitives.md#heightfield) and
[manual.md](manual.md#a-landscape-the-file-computes).

## The data is the interesting half

`mesh` was the first primitive whose parameter was a file. This is the first whose parameter is a
**grid**, and the obvious way to fill one is an image. That is not what happens here, and the
reason is worth stating before anything else.

`src/Chroma/Rendering/PngWriter.cs` is hand-rolled and writes only, so an image would need the
first image **decoder** in this solution. It would also give up something this project spends a
great deal of effort on: a scene here is reproducible from the file that describes it, which is
what the manual's byte-identity check and every cross-driver sweep rest on. A terrain that arrives
from a `.png` is reproducible only if the `.png` travels with it.

`perlin` was already built, deterministic on every machine by construction, and evaluated at bind
time. So the samples come from the scene, and the primitive takes them two ways:

```js
heightField { height: terrain, resolution: 256 }        // a function, called per sample
heightField { heights: [[0, 1, 0], [1, 2, 1], [0, 1, 0]] }   // the numbers themselves
```

**Exactly one of the two.** Neither is not a shape, and both is two answers to one question with
no rule for which wins.

### Why the function is called with coordinates and not indices

`height(x, z)` is called with `x = -1 + 2i/resolution`, not with `i`. That single choice is what
makes `resolution` mean *how finely* rather than *what shape*: raising it refines the same
landscape instead of describing an unrelated one, and a scene can tune it for the camera without
touching the terrain. Index arguments would make `resolution: 128` and `resolution: 256` two
different worlds, and the field would be a worse way of writing a nested loop rather than a better
one. `HeightFieldTests.Samples_the_same_terrain_at_two_resolutions` pins it.

### What it cost the evaluator

A binder had no way to call a function. `Evaluator.EvaluateCall` resolves a name, evaluates
argument *expressions* in the caller's scope, checks the arity and then runs the body; a binder
holds the callee and the argument *values* already, so only the last part applies. That tail is now
`Evaluator.Invoke`, and `BindingContext` carries the evaluator so a binder can reach it.

**Re-entering the evaluator after binding has started is safe**, and it is worth writing down why
rather than leaving it to be rediscovered. Binding runs after `Execute` has returned, so the return
flag is clear, the return value is null and the call depth is zero, and `Invoke` leaves all three
as it found them. `_missingReturnReported` is keyed on the function, so a body that falls off its
end reports once over a whole grid rather than a million times.

A built-in routes through the same call, which is what makes `height: perlin` legal beside
`height: terrain`. A field that takes a function should not care which kind of function it was
handed.

### The other form is for small grids, and the language says why

`heights:` reads the numbers directly. It is the right form for a grid a scene already has, and
the wrong form for a fine one, because arrays in this language are values: `a[i] = x` rebuilds the
array rather than writing into it. Filling a 257 by 257 grid with a nested loop is quadratic in the
row length per assignment. That is a property of the language rather than of this primitive, and
[scene-primitives.md](scene-primitives.md#heightfield) says so where someone about to write the loop
will read it.

## It has to be closed, and that is what the box is for

Every solid here is a CSG operand and needs a well-defined inside, which is the rule that made
iteration 6 refuse POV-Ray's `open` cones and prisms. A surface is not a solid. So the primitive is
the volume **under** the terrain, walled at the footprint's edges and floored at `base`, which is
what makes `difference { terrain, sphere }` mean something: a crater with a lit interior rather
than a window through to the sky.

The footprint is fixed at `[-1, 1]` in x and z, and the heights are the numbers the scene wrote,
unscaled. **Nothing is normalised into POV-Ray's unit cube**, because normalising needs the grid's
own extremes in the transform: two terrains of different amplitude would then render identically,
and a flat field would be a scale of zero surfacing as a matrix that cannot be inverted. `lathe`
already says its canonical form is itself and a mesh is not a unit anything; this follows them.

The consequence for the tracing is larger than it looks. Clip the ray to the box first, and
**inside that interval the solid is exactly `y ≤ H(x, z)`**. The four walls and the floor never
have to be intersected: they are the box's own faces, and the two ends of the clip already name
them. One point test at the entry says whether the ray starts inside, and that single boolean is
the whole of the bottom half of the solid.

## The two ties, and why a hair of slack settles both

This is the part of the iteration that was not visible from the plan, and it produced the only
wrong picture the work had to chase down.

**The floor may not sit level with the lowest sample.** The default `base` is the lowest sample,
which is what makes `heightField { height: terrain }` a solid on its own without the scene having
to say where the ground is. Level with the minimum, though, the solid has zero thickness wherever
the terrain reaches its own floor, and a terrain with a flat bottom, which is any function that
clamps, reaches it over an **area** rather than at a point. A ray entering there is neither inside
nor outside; the parity that defines the solid turns on the last bit of `origin + t * direction`,
and the answer differs between the camera ray and the shadow ray that leaves the same point. The
symptom was a band of the surface shadowing itself, moving with the light and not shrinking with
resolution, which is what made it look like a shading bug for as long as it did.

So the default floor sits a ten-thousandth of the terrain's own height **below** the lowest sample.
The picture cannot show the difference and the question stops being asked.

**The lid may not sit level with the tallest sample either**, for the same reason at the other end,
and there the fix is free: the lid is not a surface of the solid at all, only the top of the box
the march is clipped to, so lifting it changes no geometry. `GeometryEmitter.Lid` is that
ten-thousandth.

Both are relative to the solid's own height rather than absolute, because a terrain may be a
millimetre or a kilometre tall before its transform, and both are floored at one unit so that a
flat field still gets a lid it can be told apart from.

## The block in the shape buffer

Written by `GeometryEmitter.AppendHeightField`. Every offset is relative to the header, which is
what lets two placements share one copy.

```
[0]  (cells, flags, maxSteps, 0)          flags bit 0: shade with interpolated normals
[1]  (heightAt, 0, base, lid)
     heights: (cells + 1)^2 samples, FOUR to a texel, row major with z outermost,
              the tail padded with the last sample
```

**Four samples to a texel, not one.** A height is a single number, so a texel apiece would spend
three lanes on nothing: at the cap that is 16.8 MB against 4.2. The shader picks the lane with a
chain of `?:` and never with a variable index, which is `meshPermute`'s rule, and it does it inside
`hfHeight` so the emitted body never pays for it.

**There is no acceleration structure, and that is the difference from a mesh.** A mesh needs a tree
because its triangles are in no order. A grid *is* its own index: the march visits exactly the
cells the ray crosses, in order. What that costs is the grazing ray, which walks cells it is far
above; what it saves is the second tree, the second packing and the second set of tie-break
arguments. A min-max pyramid over blocks of cells would buy frame time and cannot buy budget,
because the march already costs the budget nothing, so it is recorded in
[suggestion.md](suggestion.md) with the measurement that would justify it rather than built here.

**Blocks are interned by content**, in `SceneTables.BlockOffsets`, which was `MeshOffsets` until
this iteration. The table was already keyed on a signature naming a content, and nothing about a
mesh's content distinguished it from a height field's, so the rename is the whole change.

## The signature, again

A mesh needed a content hash because two roots are decided to be **one shape** by emitting each
into a throwaway `GeometryEmitter` and comparing the GLSL text, and a mesh's body carries one
literal offset into a buffer that starts empty inside a probe. Every mesh sits at offset zero
there, so any two of them emit identical text.

A height field has exactly the same shape of body and exactly the same trap, so it carries exactly
the same fix: sixteen hex characters over the cells, the samples, the floor and the smooth flag,
appended to `LeafEmitter.KeyOf` and written into the emitted body as a comment.

```glsl
// heightField 6f3a1c9e0b47d2a5
```

The hashing itself moved into `Chroma.Core.Assets.ContentSignature`, which `MeshFile.Signature` now
uses as well. Two callers, one implementation, and one place to read what the trap is.
`HeightFieldTests.Tells_two_different_height_fields_apart` is the regression test and it fails
loudly without the signature.

What the signature covers is what the block holds and nothing else. The floor is in because it is
in the header and it changes the solid; the smooth flag is in for the same reason. **How the grid
was arrived at is not**: the same numbers from a function and from a literal are the same field and
share one upload.

## Smoothing stores nothing

`smooth: true` interpolates normals across a cell, which is iteration 7's lesson repeated: the
tessellation is in the shading before it is in the silhouette, and a faceted landscape reads as a
coarse one when it is a missing interpolation.

**Where a mesh had to upload them, this computes them.** A mesh's vertex normals come from a file
and cannot be derived without knowing what the file meant. A height is a function of two
coordinates, so the normal at a sample is a central difference over its four neighbours and the
shader has it for four fetches at the hit. That saves an array larger than the heights themselves,
it runs once per shaded point rather than once per scene, and it means the flag changes one
function in the shader and nothing at all about the packing.

The one honest caveat is the border. The difference is one-sided on the outermost ring of samples,
because the neighbour outside the grid does not exist, so the normal there is slightly different
from what an interior sample of the same slope would give. Extrapolating a ghost ring would be a
guess about data that was never given.

## What it costs

Measured on `scenes/terrain.chroma`, an RTX 4070 SUPER, an island and the sea it sits in, both
built from one 256 by 256 field:

| | |
| --- | --- |
| Samples | 66,049, uploaded once for two placements |
| Block | 16,515 texels, 264 KB |
| Estimated cost | 329 statements, 1% of the instruction budget |
| Widest root | 5 spans |

**A height field costs the program almost nothing and grows only in memory**, which is the mesh's
property and for the same reason: the march takes its bound from the shape buffer rather than from
a literal, so the driver compiles one step instead of one per cell, and iteration 15 counts a loop
bounded by a runtime value at a constant. One leaf body is about 104 statements whatever the grid
holds. `HeightFieldTests.Costs_the_same_however_fine_the_grid_is` asserts it directly: two fields
differing a hundredfold in texels report the same `ShapeReport.Cost`.

What does grow, and what nothing else in this renderer spends, is **load time**. Every sample is a
call of a function the scene wrote, through a tree-walking interpreter, and a five-octave fractal
body is five `perlin` calls and a dozen arithmetic nodes apiece:

| Cells | Samples | Load |
| --- | --- | --- |
| 128 | 16,641 | under a tenth of a second |
| 256 | 66,049 | about a sixth of a second |
| 512 | 263,169 | about two seconds |
| 1,024 | 1,050,625 | about nine seconds |

That is why the default is 128 and why `GpuLayout.MaxHeightFieldResolution` is 1,024. The cap
bounds memory the way `MaxMeshTriangles` does, at 262,657 texels and 4.2 MB, but what it is really
protecting is the wait: past a million calls a scene should hear a diagnostic naming the field
rather than sit there. Iteration 18 removed the evaluator's iteration and call budgets on the
grounds that `scenes/cube-4.chroma` legitimately spends 328,419 iterations, so a load in this range
is inside what the project already accepts.

## What was left out

**Image files.** The decoder is owed three times over now, by this, by a skybox and by textures,
and it should be built once for all three rather than for whichever comes first. See
[suggestion.md](suggestion.md).

**A min-max pyramid.** Priced above: it buys frame time on grazing rays and cannot buy budget.

**Rectangular grids.** A height field is square, which keeps `resolution` one number and the
diagnostics short. The header has a spare lane for the second count, so relaxing it later costs no
format change.

**An exact span bound.** `maxSpans` is declared by the scene rather than derived, exactly as a
mesh's is, and it is one of the two bounds in this renderer that are not proofs. A ray grazing a
ridge line enters and leaves once per undulation. See the note in
[csg-raytracing.md](csg-raytracing.md#height-field-a-march-over-known-data).

**A distance field.** `--sdf` refuses a height field, for the reason it refuses a mesh: the exact
distance is a search over cells with a point-to-triangle test in each, plus a sign, and the sign is
a ray cast, and a ray cast is the span backend. See [raymarching.md](raymarching.md).
