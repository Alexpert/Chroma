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
| 23 | Rounding error, as a subject rather than a constant | not started |
| 24 | Meshes | not started |
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

The shader carries two hand-chosen tolerances, `EPS` at 1e-4 and a larger shadow bias, and the
comment beside the second already says why it is larger: the hit point's rounding grows with `t`.
PBRT chapter 6.8 is the rigorous version of that thought, conservative error bounds carried
through the intersection arithmetic and spawned rays offset by a bound rather than by a number
someone picked. This is not a feature, and it is what shadow acne, self-intersection and a thin
solid that vanishes at distance all are. Iteration 6 met this class of problem three times in one
iteration, and 24 and 25 both meet it again.

## 24. Meshes

The largest primitive this renderer could gain, and the entry that has to start by saying what a
mesh is not: a solid. Every shape here is a CSG operand and needs a well-defined inside, which is
the rule that refused POV-Ray's `open` cones in iteration 6. A triangle soup has no inside; a
closed, manifold, consistently oriented mesh has one, by parity of crossings along the ray. So the
primitive accepts the second and refuses the first with a diagnostic, and the refusal is a real
piece of work, because "is this mesh closed" is a question about the file rather than about a
field.

1. **It must return spans, not the nearest hit.** This is the point that makes mesh tracing here
   different from mesh tracing anywhere else. A CSG operand has to hand back every interval the ray
   spends inside it, so the traversal cannot stop at the first triangle and cannot use the
   front-to-back early-out that makes a BVH fast in an ordinary ray tracer. It collects all hits,
   sorts them, and pairs them. The even-odd crossing test that settles a prism's or a lathe's
   contour is the two-dimensional version of exactly this, so the shape of the code already exists.
2. **The rounding problem is the one iteration 6 already met.** A ray through a shared edge hits
   twice or not at all, and either answer breaks the parity that defines the inside. That is the
   lathe's duplicate-crossing bug in three dimensions, fixed there by half-open ranges so that each
   edge owns one of its endpoints. PBRT chapter 6.8 covers watertight ray-triangle intersection
   specifically, which is what makes 23 above the entry that comes first.
3. **A per-mesh BVH, and the good news is the cost model.** `InstanceBvh` exists but is a tree over
   *placements*, not triangles, so this is a second one. Iteration 15 counts a loop bounded by a
   runtime count as a constant, and a BVH walk is precisely that, which is the mechanism iteration
   14 used to get under the instruction ceiling in the first place. A million-triangle mesh should
   therefore cost almost nothing in instructions and a great deal in memory and bandwidth. The
   existing size caps in `GpuLayout` are tuned for tens of entries and have nothing to say about
   this.
4. **Another decoder, and this one brings something back.** OBJ is text and parses in an afternoon;
   glTF and PLY are binary and are the better long-term answer. What makes a mesh worth more than
   the parsing costs is that it arrives with **UV coordinates and vertex normals**, which is the
   one thing no CSG solid has. The PBR texture entry in [suggestion.md](suggestion.md) spends its
   first point on the absence of UVs; a mesh is the shape that has them, so the two features want
   each other.
5. **Smooth normals repeat iteration 7's lesson.** Interpolating vertex normals across a triangle
   is the same fix as blending normals across a flattened Bézier joint, for the same reason: the
   tessellation is in the shading before it is in the silhouette, and a faceted mesh reads as a
   coarse mesh when it is a missing interpolation.

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
