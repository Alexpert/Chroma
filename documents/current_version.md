# Current version

What the next delivery contains, and where each piece of it stands. Updated while the work
happens rather than written up at the end. What was delivered before is in the status table of
[roadmap.md](roadmap.md); what is proposed and not scheduled is in
[suggestion.md](suggestion.md).

## Target

**0.22.0**, not yet cut. `Directory.Build.props` and `editors/vscode/package.json` both read
`0.22.0`; the archives in `dist/` were built from `0.20.0`, which is what shipped.
[tools/publish-release.ps1](../tools/publish-release.ps1) reads the version from
`Directory.Build.props`.

Geometry and primitives is the theme. Four entries moved here out of
[suggestion.md](suggestion.md), and they are one delivery because each of the last three depends
on what the ones before it settle.

| # | Deliverable | State |
| --- | --- | --- |
| 21 | Documentation rules, and the manual in the archive | done |
| 22 | The geometry the existing primitives are missing | done |
| 23 | Rounding error, as a subject rather than a constant | done |
| 24 | Meshes | done |
| 25 | A height map | not started |

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

POV-Ray's `height_field`, and the first primitive here whose parameter is a *grid* rather than a
handful of numbers. It is worth its own iteration rather than a line in 22 because four of the
assumptions this renderer is built on meet it at once.

1. **It has to be closed.** Every solid here is a CSG operand and needs a well-defined inside,
   which is the rule that made iteration 6 refuse POV-Ray's `open` cones and prisms. A surface is
   not a solid, so the primitive is the volume *under* the surface, walled at the edges and floored
   underneath. That is what POV-Ray does and for the same reason, and it is also what makes
   `difference { terrain, sphere }` mean something: a crater.
2. **Where the samples come from is the interesting half.** An image file would need the first
   image *decoder* in this solution, since `src/Chroma/Rendering/PngWriter.cs` is hand-rolled and
   writes only. A grid computed by `perlin` at bind time needs no I/O at all, and `perlin` is
   built, so that half is already available: it has a property an image does not, in that the
   terrain is reproducible from the file that describes it, which is the assumption every
   byte-identity check in this project rests on.
3. **The data has somewhere to go, and the cap does not.** The shape buffer already carries prism
   edges, lathe edges and blob components, as an SSBO on the 4.6 path and a texture buffer on the
   3.3 fallback. Iteration 7 gave every kind an explicit size limit, enforced in the binder where
   a diagnostic can name the field rather than in a shader the driver would refuse, and those
   limits are tuned for tens of entries: `GpuLayout` allows 64 contour points, 32 sweep spheres and
   16 blob components. A 512 by 512 grid is 262,144 samples. The mechanism fits and the number has
   to be chosen rather than inherited.
4. **Tracing it is a bounded march, and that is not a reversal.** A ray walks the cells it crosses
   in order, a DDA over the grid, and solves exactly inside the cell it is in. The silhouette stays
   exact per cell, which is what iteration 0's choice of analytic intervals was protecting, and it
   is a march over known data rather than an SDF sphere trace towards an unknown surface.
   [raymarching.md](raymarching.md) is where that decision is already being reopened and priced, so
   this entry should be read against it rather than as overturning anything on its own.
5. **The cost model takes it well, which is counter-intuitive.** Iteration 15 counts a loop bounded
   by a literal at its trip count and a loop bounded by a runtime count at a constant. A DDA
   bounded by the grid size is the second kind, so a shape's cost does not grow with its
   resolution: the *data* grows, and the instruction ceiling does not count data.
6. **The span budget is what to watch instead.** A ray grazing a ridge enters and leaves the solid
   several times over, which is the non-convex case prism and lathe already brought in iteration 6,
   at a resolution where the count is bounded by the terrain rather than by a vertex list.

## Before the delivery

- [ ] Every new node, field and function documented in [manual.md](manual.md) and
      [scene-language.md](scene-language.md), illustrated where it is geometric
- [ ] The version bumped in **both** places it lives: `<Version>` in
      `Directory.Build.props`, which the two scripts read, and `"version"` in
      `editors/vscode/package.json`, which only warns when it disagrees
- [ ] `powershell -File tools/build-manual.ps1 -Check` clean
- [ ] `dotnet test` clean
- [ ] [roadmap.md](roadmap.md) and [README.md](../README.md) updated for everything above
- [ ] `powershell -File tools/publish-release.ps1`, archives checked on at least one platform
- [ ] `dist/release-notes.md` reread before it is pasted into the release form
