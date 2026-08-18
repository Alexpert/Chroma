# Current version

What the next delivery contains, and where each piece of it stands. Updated while the work
happens rather than written up at the end. What was delivered before is in the status table of
[roadmap.md](roadmap.md); what is proposed and not scheduled is in
[suggestion.md](suggestion.md).

## Target

**0.26.0**, built and not yet uploaded. `Directory.Build.props` and `editors/vscode/package.json`
both read `0.26.0`, and `dist/` holds the four archives, the `.vsix` and the release notes that
[tools/publish-release.ps1](../tools/publish-release.ps1) produced from them. What is left is the
part the script leaves alone: `git tag v0.26.0`, the draft on GitHub, and the archives attached
to it. The last version uploaded is 0.20.0.

**0.25.0 was built and never uploaded.** Iteration 26 landed before the release was cut, so the
delivery carries it too and the number moved rather than a second release going out a day apart.
Its archives are still in `dist/` and are superseded by the 0.26.0 ones beside them.

Geometry and primitives is the theme. Four entries moved here out of
[suggestion.md](suggestion.md), and they are one delivery because each of the last three depends
on what the ones before it settle. Iteration 26 is the language catching up with them.

| # | Deliverable | State |
| --- | --- | --- |
| 21 | Documentation rules, and the manual in the archive | done |
| 22 | The geometry the existing primitives are missing | done |
| 23 | Rounding error, as a subject rather than a constant | done |
| 24 | Meshes | done |
| 25 | A height map | done |
| 26 | Arrays that grow | done |

Every iteration in the delivery is built. What is left is the release itself, below.

**The order is a proposal, not a constraint.** 22 is first because it reuses machinery iteration
7 already built and needs nothing new. 23 comes before 24 and 25 because both of them land on it:
a mesh needs watertight ray-triangle intersection to keep its parity, and a height map marches a
grid whose cell boundaries have the same problem. Meshes before the height map, because the
height map's tracing is a bounded march over data and the mesh is the one that has to answer
"what is a solid" first.

## 21. Documentation rules, and the manual in the archive

**Done, and it ships with this version** unless a release is cut before it. Public and dev
material had no written boundary, and the release archives carried a `README.md` whose images and
links pointed at files that were not in them.
[documentation-rules.md](documentation-rules.md) writes the rules down once, the backlog moved
out of the roadmap into [suggestion.md](suggestion.md), this document appeared, and
`publish-release.ps1` now packages the public documents with their images and fails the build if
a link one of them kept relative does not resolve inside the archive. Recorded in
[roadmap.md](roadmap.md).

## 22. The geometry the existing primitives are missing

**Done.** Five deliverables, in the order they were built:

1. **Several contours per solid**, for `prism` and `lathe`. The span path needed nothing at all:
   it already sorts the ray's crossings of every wall and pairs them, and pairing sorted
   crossings *is* the even-odd rule, so a contour drawn inside another is a hole with nothing
   downstream knowing it happened. What it cost was closing each contour back to its own first
   point instead of the solid's, one more level of array nesting in the binder, and a header
   texel in the shape buffer holding the contour count and the ranges, so that a normal blended
   across a joint does not blend with an unrelated contour.
2. **Bézier outlines for `prism`**, which after the first was a binder change and no GLSL at all.
   The smooth-normal flag moved out of the sign of the segment count and into that new header,
   and the prism got it for free by reading the same header.
3. **Curved paths for `sphereSweep`**. A path is not a contour: it does not close, its first
   control point is a real point of the result, and it must not drop a repeated last point, so
   the flattener is its own rather than `ReadBezier` at a different arity. The radius is the
   fourth component of the same cubic. `steps` defaults to 4 rather than 8 because each step is a
   round cone, not a line segment.
4. **Cylindrical blob components**, as `blobCylinder`. The field falls off with the distance to a
   segment, which is piecewise in three regions, but in every region the squared distance is
   still quadratic in the ray parameter, so the quartic and `solveQuartic` are untouched. What
   changes is the breakpoints: four per capsule rather than two, its own entry and exit plus the
   two places the foot of the perpendicular passes an end.
5. **`quadric`**, beside the sphere, cylinder and cone rather than subsuming them. Ten
   coefficients, one quadratic solve, and the case a cone throws away: with a negative leading
   coefficient the inside is two half-infinite spans, so it is budgeted at two.

Recorded in [roadmap.md](roadmap.md).

## 23. Rounding error, as a subject rather than a constant

**Done.** The shader carried two hand-chosen tolerances, `EPS` at 1e-4 and `SHADOW_BIAS` at 1e-3,
and the comment beside the second already said why it was larger: the hit point's rounding grows
with `t`. Neither survives. PBRT chapter 6.8 is the rigorous version of that thought, and the
derivation is written up in
[csg-raytracing.md](csg-raytracing.md#rounding-error).

Four things were built:

1. **`tTolerance(t)`, at every comparison on `t`.** `gamma(5)` over the `t` being compared plus
   the ray origin's own magnitude expressed in `t`, the second because at `t` near zero what the
   point carries is the rounding of the origin rather than of `t`. It replaces `EPS` in the
   sliver guard, the union's coalescing test, both of `resolve`'s ends, `occludes` and
   `boundHit`. The origin term is a global set once at the top of `traceScene`, under the rule
   that already makes every scratch array in the geometry path a global.
2. **A measured deviation instead of a propagated bound.** `primitiveNormal` now returns how far
   the point actually is from the surface it is meant to be on, `|F| / |grad F|`, which every one
   of its branches already had the gradient for. That is the residual of the solver, the
   cancellation, the reconstruction and the transform at once, and it is what a forward bound
   through Cardano plus a guarded Newton polish could not have given usefully. Converting it to
   world units costs one divide, by the length `hitNormal` already computes.
3. **`offsetOrigin`, at all three ray-spawning sites.** PBRT's `OffsetRayOrigin`: the bound
   projected onto the normal, signed by the direction actually taken, then each component nudged
   one ulp further out through `floatBitsToInt`. The sign used to be written out by hand at two
   of the three sites, and getting it wrong renders glass perfectly black.
4. **Three tolerances deleted rather than replaced.** The cylinder, the cone and the prism each
   decided "cap or side" by testing `p.y` against `EPS`; they take whichever surface the point is
   nearest, which is the question that test was approximating.

**Not built, and deliberately.** A per-leaf transform-error constant baked by the emitter was
planned and dropped: the world-space origin term already covers the cases it would have, more
conservatively, and it would have cost an instruction in every leaf, which is the resource this
shader runs out of.

**Verified.** `dotnet test` clean at 691, with `RoundingTests` new over the seam: no scene emits
an absolute tolerance, every span comparison is sized from the `t` it compares, and `traceScene`
sets the ray scale before anything reads it. `scenes/shapes.chroma` moved 100,000 units from the
origin renders identically to the same scene at the origin; before this it came back with acne
over every solid, rings across the blob, and the bored prism's lit face black with its bore lost.
Two tolerances are left and both are now relative rather than absolute: the contour sign probe,
which sizes a step for a boolean, and the shadow walk's advance, which is the one spawned ray
with no normal available.

## 24. Meshes

**Done.** `mesh { file: "assets/teapot.obj" }` is a solid like any other, and `.obj` and `.stl`
are read, both encodings of the second. The models are committed under `scenes/assets/`, so the
scene runs from a fresh clone. Written up in [meshes.md](meshes.md); the tracing is in
[csg-raytracing.md](csg-raytracing.md#mesh--parity-in-three-dimensions).

All five points held. Three of them were more work than the entry expected and one was a fault
the entry could not have seen:

1. **Spans rather than the nearest hit**, exactly as written, and the prism's even-odd rule is
   the code it was modelled on. What the entry did not say is that `boundHit` could not be
   reused for the node test: it takes a `limit` and drops a box beginning past the nearest hit
   so far, which is the front-to-back early-out this cannot have. `meshBoxCross` is the same
   slabs without it.
2. **The tie-break is the lathe's, one dimension up.** PBRT 6.8's shear and permutation are
   functions of the ray alone, so two triangles sharing an edge get exact negations of each
   other's edge functions. Where PBRT reaches for double precision on an exact zero, GLSL 3.30
   has none, so an antisymmetric rule on the directed edge settles it, which is the half-open
   range again and needs no wider arithmetic.
3. **The cost model took it as predicted.** 112,402 triangles is 105 statements, and
   `scenes/meshes.chroma` with four meshes is 343, one percent of the budget. `InstanceBvh`
   became `Bvh`: it was always a hierarchy over a list of boxes that knew nothing else about
   them, so the triangle tree is the same call. `GpuLayout` gained `MaxMeshTriangles`, and it
   is the first limit there that bounds **memory** rather than emitted source.
4. **The decoder was the small half**, which is the entry's one misjudgement. What cost the time
   is that "is this mesh closed" has three distinguishable answers and only one of them is
   repairable. `close: true` fills holes with a fan; inconsistent winding and non-manifold edges
   are refused whatever it says. Vertex normals landed, UVs did not: nothing reads them until
   textures do, and OBJ indexes them separately from positions, which is a complication with no
   payer. Both are in [suggestion.md](suggestion.md).
5. **Smooth normals repeat iteration 7's lesson**, and the bunny is the clearest case in the
   repository: its triangles are smaller than a pixel, so the faceted version reads as noise
   rather than as facets.

**The fault that was not in the entry.** Two roots are decided to be one shape by comparing the
GLSL they emit. A mesh's geometry is not in its GLSL, and inside the probe that does the
comparison every buffer starts empty, so every mesh emits the same offset and any two of them
compare equal. A teapot and a bunny in one scene would have been drawn as one shape, with nothing
to say so. A content hash on `LeafPlan`, written into the body as a comment the cost model does
not count, fixes it; `MeshTests.Tells_two_different_meshes_apart` fails loudly without it.

**Verified.** `dotnet test` clean at 715, `MeshTests` new over every seam: the OBJ face forms
including negative indices and polygon fans, both STL encodings, welding an STL cube back to
eight vertices, each of the three refusals, `close` repairing the repairable one, the
shape-buffer offsets, the escape indices, one upload for two placements of one model, and equal
cost for two meshes differing fourfold in size. `scenes/meshes.chroma` renders.

**What it cost.** The test suite went from 3 seconds to 2 minutes 11. The picture is right; the
loader simply has no memory, and re-reads, welds and hierarchises the bunny once per node and
again per probe. Recorded in [suggestion.md](suggestion.md) with the fix, which is two
dictionaries and no design change, since the packed block is already position-independent.

Recorded in [roadmap.md](roadmap.md).

## 25. A height map

**Done.** `heightField { height: terrain, resolution: 256 }` is a solid like any other, and its
grid comes from the scene rather than from a file: a function called once per sample, or the
numbers written out. `scenes/terrain.chroma` is an island of five-octave `perlin` with a crater in
it and the sea around it, both built from one field. Written up in
[height-fields.md](height-fields.md); the tracing is in
[csg-raytracing.md](csg-raytracing.md#height-field-a-march-over-known-data).

All six points held, one of them more cheaply than the entry expected, and there was a fault the
entry could not have seen.

1. **It has to be closed**, and the box is what closes it. Clip the ray to the footprint and the
   solid is exactly `y ≤ H(x, z)` inside that interval, so the four walls and the floor never have
   to be intersected: they are the box's own faces and the two ends of the clip already name them.
   One point test at the entry says whether the ray starts underneath. That is the prism's slab
   test one dimension up, and it is why the entry's "walled at the edges and floored underneath"
   cost no code at all.
2. **The samples come from the scene**, as written. What the entry did not say is that a binder
   could not call a function: `Evaluator.EvaluateCall` resolves a name and evaluates argument
   *expressions*, and a binder holds the callee and the values already. The tail became
   `Evaluator.Invoke`, `BindingContext` carries the evaluator, and a built-in routes through the
   same call, so `height: perlin` is a landscape in one line. Calling with **coordinates rather
   than indices** is the choice that makes `resolution` a dial for detail instead of a different
   world at every setting.
3. **The number had to be chosen**, and it is 1,024 cells a side rather than the entry's 512:
   1,050,625 samples, 262,657 texels, 4.2 MB. What it really bounds is not the card but the
   **wait**, since every sample is a call of a scene function through a tree-walking interpreter
   and a million of them is nine seconds. The default is 128.
4. **The march is a DDA**, as written, and the one thing it needed that the entry did not name is
   grid space: scaling the footprint to `[0, cells]` leaves `t` untouched and makes a cell corner
   a small integer, exact in a float, so two cells sharing an edge shear identical bits and
   iteration 23's argument has nothing left to assume. `meshHit` split into `triangleCross` plus
   three fetches, so there is one watertight test and one tie-break in the file rather than two.
5. **The cost model took it**, exactly as predicted: about 104 statements whatever the grid holds,
   and `scenes/terrain.chroma` is 329 for the whole scene. Smoothing turned out to cost *nothing*
   as well, which the entry did not foresee: a height is a function of two coordinates, so a
   normal is a central difference at the hit rather than an array to upload.
6. **The span budget is what to watch**, and `maxSpans` is declared exactly as a mesh's is. A
   height field closes an odd count at the box exit rather than dropping it, which a mesh cannot
   do, so the failure is a slice missing rather than a span that never closes.

**The fault that was not in the entry.** The default floor was the lowest sample, which is what
makes a terrain a solid without the scene saying where the ground is. Level with the minimum,
though, the solid has zero thickness wherever the terrain reaches its own floor, and any function
that clamps reaches it over an **area**. A ray entering there is neither inside nor outside, the
parity turns on the last bit of `origin + t * direction`, and the camera ray and the shadow ray
leaving the same point disagree. It rendered as a band of the surface shadowing itself, moving
with the light and not shrinking with resolution, and so read as a shading bug until the normals,
the offset, the span budget and the geometry had each been ruled out. The floor now sits a
ten-thousandth of the terrain's height below the lowest sample, and the lid the same distance
above the tallest, which is free because the lid is not a surface of the solid at all.

Found on the way: `LeafEmitter.Body`'s `switch` ended in `default: SphereSweep(...)`, so a kind
added to the enum and forgotten there compiled, rendered, and was quietly the wrong solid. It now
throws.

**Verified.** `dotnet test` clean at 741, `HeightFieldTests` new over every seam: both source
forms, the same terrain agreeing at two resolutions, a built-in as the function, each refusal, the
floor's default, the block laid out as the shader reads it, four samples to a texel, one upload
for two placements, a smooth field and a faceted one being two blocks, equal cost for two grids
differing a hundredfold in texels, two different fields told apart, and a `difference` compiling
at the right width. `scenes/terrain.chroma` and `scenes/manual/primitive-heightfield.chroma`
render.

Recorded in [roadmap.md](roadmap.md).

## The reference, rewritten and split

The reference had grown into an essay about its own design: a reader who wanted to know what to
write and what would come out had to find it between the rationales, the iteration history and
the POV-Ray appendix. It is now four documents, and each entry says only what a user needs:
what the thing is for, every field with its type and default, every form each field accepts, an
example, an illustration, and what it refuses.

| Document | What is in it |
| --- | --- |
| [scene-language.md](scene-language.md) | values, operators, bindings, functions and recursion, arrays, structs, `if`, `for`, `import`, the built-ins, the grammar |
| [scene-primitives.md](scene-primitives.md) | the thirteen shapes, field by field, with the input forms each one accepts |
| [scene-composition.md](scene-composition.md) | the operators, `object`, the modifiers, inheritance, the axes |
| [scene-appearance.md](scene-appearance.md) | `camera`, `render`, the two lights, every field of `material` |

Illustrated by 41 new plates in `scenes/reference/`, rendered into
`documents/images/reference/` by [build-manual.ps1](../tools/build-manual.ps1): one per
primitive, and one per option whose result looks different. The design and history that came out
of it lives in the roadmap and the dev documents, which is where it belonged.

## 26. Arrays that grow

**Done.** An array could hold anything and nest, and its length was whatever the literal said:
there was no `push`, no `concat`, and assigning outside the bounds was reported. A list whose
length is not known where it is written, which is what a loop that keeps some of what it makes
produces, could not be built at all. Three forms close that, and none of them is a new kind of
value.

1. **`a.push(v);` is a statement**, next to `i++` and to `a[0] = x`, and never an expression. A
   qualified call is how a module is reached and deliberately not a method call, so nothing about
   `ResolveThroughModule` changed: the parser recognises the shape, and the evaluator decides by
   what the target holds, falling back to the ordinary call when it holds a module. It rebuilds
   through `Rebuild` and rebinds through `Scope.TrySet`, which is what an assignment to an element
   already did, so `let b = a; b.push(v);` leaves `a` alone and `rows[1].push(v)` costs nothing
   extra. The path walk both share is now `Evaluator.RootedPath`.
2. **`[a..b]` is half-open**, so `[0..5]` is five elements. That is the decision this repository
   already recorded when the old `for (i in 0..n)` was replaced, and its diagnostic still
   translates the form to `i < n`. It never counts down and it has no step. The lexer needed
   nothing: `0..5` already lexed as three tokens because a number stops at a dot that is not
   followed by a digit, and `..` was kept reserved for the diagnostic that names the old loop.
3. **`array(n, value)`** is the length a literal cannot give when the count is a variable. Its
   second parameter is the first argument in this library that is not a number, so
   `BuiltinArgument` gained `Any`; every slot holds the same value, which nothing can observe
   because values are immutable and a block is a description instantiated where it is used.

**What it costs.** `push` copies, so a loop of pushes is quadratic in the length. So is filling by
index, for exactly the same reason, and neither is new: `array(n, 0)` once and then `a[i] = x` is
the cheap shape, and it is what the reference recommends. Nothing is capped, which is iteration
18's decision about budgets applied unchanged; the one exception is an infinite bound, refused
because a range that never ends is a load that never returns.

## Before the delivery

- [x] Every new node, field and function documented in [manual.md](manual.md) and in the four
      reference documents, illustrated where it is geometric
- [x] The version bumped in **both** places it lives: `<Version>` in
      `Directory.Build.props`, which the two scripts read, and `"version"` in
      `editors/vscode/package.json`, which only warns when it disagrees
- [x] `powershell -File tools/build-manual.ps1 -Check` clean, all 86 images byte-identical
- [x] `dotnet test` clean, 774 tests
- [x] [roadmap.md](roadmap.md) and [README.md](../README.md) updated for everything above
- [x] `powershell -File tools/publish-release.ps1`, archives checked on at least one platform:
      `chroma-0.26.0-win-x64` renders `scenes/terrain.chroma` and dumps `scenes/meshes.chroma`,
      and carries the six public documents with their 86 images
- [x] `dist/release-notes.md` reread before it is pasted into the release form
