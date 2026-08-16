# Ray/CSG intersection by intervals

This document is meant to be **self-sufficient**: everything needed to implement the GPU
side is here, so no further web research should be required. Sources are listed at the end
for provenance only.

## The problem with "nearest hit"

A conventional ray tracer asks a primitive one question: *what is the smallest `t > 0` at
which the ray meets your surface?* That answer is enough for a scene made of independent
objects, and useless for CSG.

Consider `difference { box, sphere }` — a box with a spherical bite taken out of it. The
nearest box surface may lie *inside* the sphere, in which case it does not exist in the
result. The nearest sphere surface is not part of the result either; the visible surface is
the sphere's *far* side, seen from inside the cavity, with its normal pointing the wrong
way. No combination of nearest-hit answers reconstructs this.

The fix, due to Roth (1982), is to change the question. A primitive returns **every**
portion of the ray that lies inside it.

## Spans

A **span** is a closed interval `[tIn, tOut]` along the ray during which the ray is inside
the solid, together with the identity of the surface crossed at each end.

```
          ray  o + t*d  ------------------------------------------->
solid A         [========]                    [=====]
                tIn    tOut                  tIn   tOut
```

A solid is fully described, for one ray, by an ordered list of **disjoint, non-touching**
spans sorted by `tIn`. That representation is *closed under the boolean operators* — the
union, intersection or difference of two span lists is another span list — which is exactly
what makes CSG composable.

Sphere, box, cylinder, cone and plane are convex, so each produces **at most one span**. The
torus, prism, lathe, blob and sphere sweep are not, and produce a list of their own — see
[Primitive spans](#primitive-spans) for how many.

A span end may also lie at infinity. Only `plane` produces one directly, but the complement
below produces them too, and the encoding has to say so either way.

### Why the surface identity is stored and the normal is not

The obvious span layout carries a full surface record at each end: position, normal,
material. That is 8+ floats per endpoint, multiplied by `MAX_SPANS`, multiplied by the
stack depth — far too much register pressure for a fragment shader.

Instead a span stores only the **index of the primitive** that produced each endpoint:

```glsl
struct Span {
    float tIn;
    float tOut;
    int   surf;     // two encoded primitive references, sixteen bits each
};
```

The two references share one int, which is not a detail. A span list holds `MAX_SPANS` of
these, `MAX_STACK` lists are live at once, and every merge needs one more — 132 words, far past
what a fragment shader keeps in registers, so the whole structure lives in local memory and
every tape instruction reaches into it. Taking a `Span` from four words to three was measured
at **1.7× to 2.0× on every scene in the repository**; see
[performance.md](performance.md). Sixteen bits each is ample, since a reference is
`±(primitive index + 1)` and the instruction cap is reached long before 32767 primitives.

Once the whole tape has been evaluated and the single visible `t` is known, the normal is
recomputed from scratch: fetch that primitive, transform the hit point into its local
space, evaluate its analytic normal, transform back. This happens **once per pixel**, not
once per span.

The encoding packs the flip flag into the sign, and reserves `0` for "no surface":

```
surf = 0                 -> none: an end at ±infinity, from a complement or from a plane
surf = +(primIndex + 1)  -> outward normal, as computed by the primitive
surf = -(primIndex + 1)  -> normal must be negated (the surface came from a subtracted operand)
```

Inside a primitive's own span function the code is only ever `1` or `0` — "my surface" or
"nothing" — and the leaf step rewrites the `1`s into the real index. A span function does not
know which primitive it is being evaluated for and should not have to.

## The three operators

The inputs are two sorted, disjoint span lists `A` and `B`. All three operators produce a
sorted, disjoint span list.

### Union — `A ∪ B`

Sorted merge with coalescing. Walk both lists in `tIn` order, keep one interval open, and
extend it while the next interval starts before the open one ends.

```
A     [====]      [======]
B        [======]           [==]
A∪B   [=========] [======]  [==]
```

When two intervals coalesce, the surviving `tOut` / `surfOut` are those of whichever
interval extends furthest. The interior surfaces simply vanish — this is correct: they are
no longer on the boundary of the result.

**This is why there is no `merge` operator here.** POV-Ray has one, and needs it: its `union`
is a shortcut that keeps every operand's surfaces without merging intervals, so two
overlapping transparent solids show the faces buried inside each other, and `merge` exists to
remove them. An interval union has no such shortcut to undo — the coalescing above *is* the
merge. Adding a `merge` keyword would register a second name for `union` and nothing else.

It has a second payoff for transmissive solids, which
[transparency.md](transparency.md#nested-and-overlapping-media) relies on: two intersecting
glass spheres produce one span with one pair of boundaries, so there is no interior boundary
to mistake for a nested medium.

### Intersection — `A ∩ B`

Two-pointer sweep. For the current pair `(a, b)`:

```
lo      = max(a.tIn,  b.tIn)     with surfIn  taken from whichever contributed the max
hi      = min(a.tOut, b.tOut)    with surfOut taken from whichever contributed the min
emit [lo, hi] if lo < hi
advance whichever of a, b has the smaller tOut
```

```
A     [==========]        [====]
B         [==========]  [==]
A∩B       [======]
```

### Difference — `A \ B`

Two equivalent formulations. Prefer the second in GLSL.

**Direct.** For each span `a` of `A`, carry a cursor:

```
cur    = a.tIn
surf   = a.surfIn
for each b of B overlapping a, in order:
    if b.tIn > cur:
        emit [cur, min(b.tIn, a.tOut)]  with surfOut = flip(b.surfIn)
    cur  = max(cur, b.tOut)
    surf = flip(b.surfOut)
if cur < a.tOut:
    emit [cur, a.tOut] with surfOut = a.surfOut
```

**Via complement.** `A \ B  ==  A ∩ complement(B)`, where the complement of a span list is
its gaps, extended to `±infinity`, with every surface flipped:

```
B            [====]      [==]
comp(B)  ====]    [======]  [=========
         -inf                     +inf
```

This is the recommended implementation: it is one small `csgComplement` function plus the
`csgIntersection` that already exists, instead of a third independent merge loop. Less GLSL,
and a single place where the interval logic can be wrong.

**The flip is the whole point.** Where a surface of `B` bounds the result, the ray is
leaving `B`'s interior into the remaining solid, so `B`'s outward normal points *into* the
result. Negating it is what makes the inside of a drilled hole shade correctly rather than
appearing black or inside-out. This is the single most commonly botched detail in a CSG
renderer — if a cavity renders unlit, check the flip first.

### Picking the visible hit

After the root span list is computed, scan it in order for the first span with
`tOut > EPS`:

- if `tIn > EPS`, the hit is `(tIn, surfIn)` — the ray enters the solid here;
- otherwise the ray **started inside** the solid; the hit is `(tOut, surfOut)` and the
  normal must be negated on top of whatever the encoding says, because a back face is being
  viewed.

If no such span exists, the ray misses everything and the background colour is used.

### Degenerate spans

A ray grazing a sphere tangentially yields `tIn == tOut`. A ray hitting a box exactly on an
edge does the same. Such spans must be **dropped** (`tOut - tIn < EPS`), because a
zero-width solid subtracted from another produces a zero-width sliver that survives
floating-point rounding and shows up as isolated speckles along silhouettes.

## Primitive spans

Transforms are baked (see below), so every primitive is evaluated in its own **canonical
local space**. For six of the eleven that removes the shape parameters entirely: the only
per-primitive data is a kind, a material index and an inverse matrix.

| Kind | Canonical form | Parameters | Spans |
| --- | --- | --- | --- |
| sphere | unit sphere, centre at the origin, radius 1 | — | 1 |
| box | axis-aligned, `[-1, 1]` on all three axes | — | 1 |
| cylinder | axis along `+Y`, radius 1, from `y = 0` to `y = 1`, capped | — | 1 |
| cone | radius 1 at `y = 0` tapering to `cap` at `y = 1`, capped | `cap` | 1 |
| plane | the half-space `y <= 0`, surface through the origin | — | 1 |
| torus | major radius 1 in the XZ plane | `minor` | 2 |
| prism | contour in XZ swept from `y = 0` to `y = 1`, capped | offset, edges | edges ÷ 2 |
| lathe | outline in `(radius, y)` revolved about `+Y` | offset, segments | segments |
| blob | components as written, in the blob's own space | offset, components | components |
| sphere sweep | spheres as written, in the sweep's own space | offset, spheres | spheres − 1 |
| quadric | the coefficients as written; there is nothing to canonicalise | offset | 2 |

A non-uniform scale on the canonical sphere gives an ellipsoid, and on the canonical
cylinder gives an elliptic cylinder. That falls out for free and is a feature, not an
accident.

### Why some parameters survive the matrix

The first three shapes are reachable from their canonical form by an affine map, so nothing
is left over. Two more are not, and for the same reason: the cone's taper and the torus's
minor radius are **ratios**, and scaling changes both radii together. One number each
therefore has to travel alongside the matrix, in the two slots the primitive record has
always had spare.

Four are defined by a **list** rather than by a formula, and the quadric by ten numbers that
would not fit either. All five go to a separate shape buffer and their slots hold an offset
instead. Keeping the primitive record a fixed stride is not negotiable — `texelFetch` indexing
depends on it — and a scene of spheres should not pay for the longest prism anyone might write.

Note what does *not* need a parameter: a prism's height, and where a lathe sits. Those are
affine and go in the matrix, which is why one contour in the buffer serves a prism of any
height.

### Sphere

With ray `o + t*d` in local space and `d` **not** normalised:

```
a    = dot(d, d)
b    = dot(o, d)
c    = dot(o, o) - 1.0
disc = b*b - a*c
if (disc < 0.0) -> no span
s    = sqrt(disc)
tIn  = (-b - s) / a
tOut = (-b + s) / a
```

Local normal at `p`: `normalize(p)`.

### Box — slab test

```
inv  = 1.0 / d                 // ±infinity for a zero component is fine and correct
t1   = (vec3(-1.0) - o) * inv
t2   = (vec3( 1.0) - o) * inv
lo   = min(t1, t2)
hi   = max(t1, t2)
tIn  = max(lo.x, max(lo.y, lo.z))
tOut = min(hi.x, min(hi.y, hi.z))
if (tIn > tOut) -> no span
```

The `±infinity` from a zero direction component works because `inf * 0` never occurs here
unless the origin lies exactly on a slab plane; a ray parallel to a slab produces
`(-inf, +inf)` or `(+inf, -inf)`, and the second case correctly fails the `tIn > tOut` test.

Local normal at `p` — deferred, so recomputed from the point by picking the dominant axis:

```glsl
vec3 a = abs(p);
if (a.x >= a.y && a.x >= a.z) return vec3(sign(p.x), 0.0, 0.0);
if (a.y >= a.z)               return vec3(0.0, sign(p.y), 0.0);
return vec3(0.0, 0.0, sign(p.z));
```

### Cylinder — an intersection in disguise

A capped cylinder is the intersection of an infinite tube with a slab, and both are already
expressible as spans. Compute the two and run `csgIntersection` on them; there is no need
for special-case cap logic.

Infinite tube of radius 1 about `+Y`:

```
a    = d.x*d.x + d.z*d.z
b    = o.x*d.x + o.z*d.z
c    = o.x*o.x + o.z*o.z - 1.0
if (a < EPS)                  // ray parallel to the axis
    -> span is (-inf, +inf) if c < 0, otherwise no span
disc = b*b - a*c
if (disc < 0.0) -> no span
s    = sqrt(disc)
tIn  = (-b - s) / a
tOut = (-b + s) / a
```

Slab `0 <= y <= 1`:

```
if (abs(d.y) < EPS)
    -> span is (-inf, +inf) if 0 <= o.y <= 1, otherwise no span
ta = (0.0 - o.y) / d.y
tb = (1.0 - o.y) / d.y
span = (min(ta, tb), max(ta, tb))
```

Local normal at `p`:

```glsl
if (p.y < EPS)        return vec3(0.0, -1.0, 0.0);
if (p.y > 1.0 - EPS)  return vec3(0.0,  1.0, 0.0);
return normalize(vec3(p.x, 0.0, p.z));
```

### Cone — the same quadric with a tilt

With `m = cap - 1`, the lateral surface is `x² + z² = (1 + m·y)²`, and the inside is where
that is negative. Substituting the ray gives `A t² + 2B t + C` with

```
g = 1 + m*o.y
A = d.x*d.x + d.z*d.z - m*m*d.y*d.y
B = o.x*d.x + o.z*d.z - m*d.y*g
C = o.x*o.x + o.z*o.z - g*g
```

then intersect with the same `0 <= y <= 1` slab the cylinder uses. `m = 0` reduces every line
of this to the cylinder's tube.

Two cases the cylinder does not have:

- **`A == 0`** — the ray runs parallel to a generator, the quadratic degenerates to a line,
  and the inside is a half-line rather than an interval.
- **`A < 0`** — the ray passes between the two nappes of the double cone, and the inside is
  the two half-lines *outside* the roots. Only one of them can survive the slab, because
  `cap >= 0` puts the mirror nappe at `y > 1`. That is also why the sign convention matters:
  canonicalising with the WIDER end as the base bounds `cap` to `[0, 1]` and keeps the mirror
  nappe out of the slab, so no explicit test for it is needed anywhere.

Local normal is the gradient, `normalize(vec3(p.x, -m*(1 + m*p.y), p.z))`, with the two caps
handled as the cylinder's are.

### Plane — a half-space, and the first span end at infinity

Canonically `y <= 0`. With `t = -o.y / d.y`, a descending ray gets `[t, +inf)` and an
ascending one `(-inf, t]`; a ray parallel to the surface is inside for its whole length or not
at all. The normal is `(0, 1, 0)` everywhere.

The infinite end bounds no surface, and the `surf = 0` code already reserved for the ends of a
complement says exactly that. Nothing else in the machinery needed changing for it — which is
worth noticing, because "add an unbounded primitive" sounds like it should be invasive.

### Quadric — the general case, and the nappe the cone throws away

`A x² + B y² + C z² + D xy + E xz + F yz + G x + H y + I z + J`, inside where it is negative.
Substituting the ray gives a plain quadratic in `t`, the same solve the sphere, the cylinder and
the cone all use.

What is different is that **there is no slab to clip it with**, and that changes the span count.
`coneSpan` also meets a negative leading coefficient, and gets away with returning one span
because its `0 <= y <= 1` slab throws the mirror nappe away. Here nothing does, so with `a < 0`
and a non-negative discriminant the inside is `(-inf, t0]` together with `[t1, +inf)`: **two**
half-infinite spans. So `quadricSpans` fills `gRoots` and returns a count, as `torusRoots` does,
rather than returning a `Span`, and the primitive is budgeted at two.

Three degeneracies, each with precedent in the file. `|a| < TINY` is a linear solve, which is
`coneSpan`'s own fallback and the lathe's horizontal segment. `|a| < TINY` *and* `|b| < TINY`
leaves the constant, so the ray is inside for its whole length or not at all. A negative
discriminant means no crossing, and which side the ray is on is the sign of `a`.

The normal is the gradient of the same expression, which does not involve `J`, and needs **no**
negation, unlike the blob's: the inside is where the expression is negative, so it rises outward
already.

The box is `Aabb.Unbounded`, as `plane`'s is, and for the same reason — a hyperboloid genuinely
runs to infinity and a box that is too small removes geometry from the image. Fitting a tight
ellipsoid box in the cases where one exists is real work with several degenerate cases, and the
language already carries the answer: `intersection { quadric box }` takes the box's bounds through
`Aabb.Intersect` and does the clipping in the same operator. That is what POV-Ray's `bounded_by`
is for, here as an ordinary CSG node.

It is added **beside** the sphere, the cylinder and the cone rather than subsuming them. Those
three arrive with a slab, a known bound and a solve of a few lines each; re-expressing them
through the general form would cost every scene that uses one instructions to buy nothing.

### Torus — a quartic

`(x² + y² + z² + 1 - minor²)² = 4(x² + z²)` with the major radius canonicalised to 1.
Substituting the ray and writing `g = d·d`, `h = 2(o·d)`, `i = o·o + 1 - minor²`:

```
c4 = g*g
c3 = 2*g*h
c2 = h*h + 2*g*i - 4*(d.x*d.x + d.z*d.z)
c1 = 2*h*i - 8*(o.x*d.x + o.z*d.z)
c0 = i*i - 4*(o.x*o.x + o.z*o.z)
```

Four roots, sorted, paired into at most two spans. Local normal at `p`: take the nearest point
on the centre circle, `(p.x, 0, p.z)` normalised, and point away from it.

**Re-origin the ray before forming the coefficients.** `c0` goes as the fourth power of the
origin's distance, so a camera ten units from a unit torus builds coefficients near 10⁴ out of
which roots near 10 have to be recovered — three or four digits of a 32-bit float gone before
the solver starts. Shifting the parameter to the ray's closest approach to the centre, and
adding the shift back to each root, costs one dot product. Without it a torus is visibly
ragged from any distance, in a way that reads as a bug in the solver rather than in its input.

### Prism — a 2D problem plus the slab

Each edge of the contour extrudes into a planar wall that the ray crosses at most once, so the
crossings are found in the XZ projection. For an edge `a → b` with `s = b - a`, and the ray's
projection `o + u·d`:

```
denom = cross(d, s)                    // skip if ~0: the ray runs along this wall
u     = cross(a - o, s) / denom        // distance along the ray
v     = cross(a - o, d) / denom        // position along the edge, kept if 0 <= v < 1
```

Sort the `u` values and pair them; clip each pair to the `0 <= y <= 1` slab, which is what the
caps are. **The `v` test is half-open on purpose.** A ray through a vertex meets two edges, and
counting it twice flips the parity of every crossing after it — the symptom is a solid that
comes out striped, or one you can see straight through.

### Lathe — a list of cone frusta sharing an axis

Each segment `(r0, y0) → (r1, y1)` revolves into a frustum. Writing the segment parameter in
terms of `y`,

```
s(t) = (o.y - y0)/dy + t*(d.y/dy)          // position along the segment
R(t) = r0 + (r1 - r0)*s(t)                 // the frustum's radius there, linear in t
```

reduces the surface to the cone's quadratic, `(o.xz + t·d.xz)² = R(t)²`, with each root kept
only if `0 <= s < 1` — half-open, for the reason above. A horizontal segment revolves into a
flat annulus and is a linear solve instead. Sort the crossings and pair them.

The normal is found in the `(radius, y)` half-plane — nearest segment, perpendicular to it —
and then lifted back out by carrying the radial component round the axis.

For both the prism and the lathe, the perpendicular's **sign** is settled by an even-odd
point-in-contour test just off the surface. Demanding one winding instead would be cheaper and
would render a counter-clockwise contour inside out, with nothing in the file to explain it.

### Several contours, which the span path already did

A prism or a lathe may hold more than one closed contour, and the tracing above needed **no
change at all** to support it. Sorting the crossings of every wall and pairing them consecutively
*is* the even-odd rule: along the infinite line the ray starts outside every bounded contour, so
consecutive pairs are exactly the interior intervals, for one contour or twenty. A contour drawn
inside another is a hole and nothing had to be told so.

Three things did have to change, and none of them is the solve:

1. **Each contour closes back to its own first point.** A last edge closing to the *solid's*
   first point would join two outlines into a figure of eight and every crossing past it would
   pair with the wrong partner.
2. **The shape buffer gained a header.** At `paramA`: `(contourCount, smoothFlag, 0, 0)`, then one
   `(start, count)` per contour, then the edges. `paramB` is the total edge count and is now
   always positive.
3. **Normal blending had to learn where the seams are.** `contourNormal` blends a joint with
   edges `e ± 1`; wrapping those modulo the whole edge list would pair the first edge of one
   contour with the last edge of another, which are not neighbours and are usually nowhere near
   each other. `insideContour` is untouched and is still run over every edge, because even-odd
   across all contours is precisely the right sign test.

The header is also what retired the trick where the smooth flag rode in the **sign** of the
segment count. That was the only slot left when the primitive record had two, and it could carry
one bit; contour ranges are several numbers and need somewhere real to live, so once there was a
header the flag belonged in it. `prism` gained blended normals as a side effect, since it reads
the same header.

### Blob — an isosurface, and why it is tractable

The surface is where `Σ strength·(1 - (d/radius)²)²` reaches the threshold. Along a ray `d²`
is a quadratic in `t`, so **each component contributes a quartic** — and a sum of quartics is
still one quartic, however many components there are. That is the whole reason this shape can
be solved exactly rather than marched.

Between two consecutive component boundaries the set of live components does not change, so:

1. Intersect the ray with each component's sphere; collect the entry and exit `t` as
   breakpoints and sort them.
2. In each interval, sum the quartic coefficients of the components live there (a component is
   live if the interval's midpoint is inside it).
3. Solve `quartic(t) = threshold` and keep the roots inside that interval — a root outside it
   belongs to a polynomial not in force there, and the neighbouring interval will find it with
   the right coefficients.
4. Sort every root found and pair them. The field is zero outside every component, so the ray
   always starts outside and the crossings pair without a parity flag.

The normal is the field's gradient, `Σ -4·strength·(1 - d²/r²)·(p - q)/r²`, negated: the field
rises towards the inside. `q` is the closest point of the component, which for a sphere is its
centre.

**Re-origin here too**, at each interval's midpoint. It matters more than for the torus, and
it simplifies the code as well — the "is this component live" test reduces to whether the
midpoint is inside its sphere.

#### Cylindrical components, which cost breakpoints and not degree

A `blobCylinder` measures `d` to the **segment** between its two ends, so it is a capsule. That
distance is piecewise in three regions, but in every one of them `d²` is still **quadratic** in
`t`, so the field is still a quartic and the solver above is untouched:

| region | `d²(t)` as `α t² + β t + γ`, with `W = o - a`, `u = b - a`, `L² = u·u` |
| --- | --- |
| foot before `a` | `α = D·D`, `β = 2(W·D)`, `γ = W·W` — the sphere's own coefficients |
| foot on the axis | the same three with the axial part removed: `α = D·D - (D·u)²/L²`, and so on |
| foot past `b` | the first row again with `W' = o - b` |

What grows is the **breakpoint count**: four per capsule rather than two. Its own entry and exit
come from `roundConeSpan` with two equal radii, which is exactly the capsule the sweep already
solves; the other two are where the foot passes each end. The foot is affine in `t`, so each is
one root of a linear equation, and because those crossings are breakpoints the region cannot
change inside an interval — one test at the midpoint settles it, beside the liveness test that
was already there.

The **crossing** bound is unchanged at two per component. `d²` is the squared distance to a convex
set and is therefore convex in `t`, so the clamped field is single-humped exactly as a sphere's
is.

Every component is stored as a capsule, a spherical one having both ends at the same point. The
shading gradient then needs no discriminator at all: clamping the foot onto a segment of no length
returns that point, so one closest-point expression covers both kinds. The span code still emits
the two kinds as two loops, so a blob of spheres alone generates what it always did.

### Sphere sweep — a union of round cones

Each consecutive pair of spheres contributes the **convex hull of the two**, a "round cone".
Because the hull is convex a ray meets it in exactly one interval, and that interval is simply
the outermost of what its three pieces give — sphere `a`, sphere `b`, and the cone tangent to
both. No merge is needed between the pieces and none of them has to know about the others.

The tangent cone is the part worth getting right. With `sinθ = (rb − ra)/|b − a|`, the tangent
line touches each sphere **off its centre**:

```
axial from a       radius
   -ra·sinθ        ra·cosθ
   |b-a| - rb·sinθ rb·cosθ
```

A cone drawn between the two centres instead would cut into both caps, and the symptom is a
visible pinch at every joint — which is precisely what a sweep exists to avoid.

The lateral surface is then the same quadratic the lathe's frusta use, with each root kept only
if its axial coordinate lies between the two tangent circles. Since `R0` and `R1` are both
non-negative there is no mirror nappe inside that range, so no test for one is needed.

The **union** of the segments is done with a depth counter rather than by pairing crossings:
consecutive hulls overlap by a whole sphere, and pairing would take a crossing buried inside
the next segment for a surface. Collect `(t, ±1)` events, sort them carrying the sign, and open
a span where the depth leaves zero and close it where it returns.

The normal is the one fact the whole shading rests on: **at a surface point the outward normal
points away from the centre of the generating sphere that touches there.** On the caps that is
an end sphere; on the band it is an interior one, and the normal tilts off radial by exactly
the cone's half-angle. Using the radial direction alone is the usual mistake, and it shows as a
tube lit as though it were a cylinder however much it tapers.

### Curves, and why they cost nothing here

A cubic Bézier outline is flattened into segments **on the CPU**, before the scene is
compiled. Nothing downstream learns a curve was involved: the model, the tape and the shader
all see a polyline. A curved lathe therefore costs exactly what a polyline lathe with the same
number of vertices costs, and no new intersection code exists for it. `prism` reads the same
fields through the same reader.

A **path** is flattened by its own reader rather than the outline's, because it differs on every
point that matters: it does not close, so its very first control point is a real point of the
result rather than the one the closing edge comes back to; it must not drop a repeated last
point, because repeating the first sphere is how a sweep is made into a loop; and it therefore
yields `1 + curves × steps` points where an outline yields `curves × steps`. The radius travels
as the fourth component of the same cubic, so a taper follows the bend. Checking the **control**
radii is the whole check that the flattened ones are positive, since a cubic stays inside the
convex hull of its control points.

`steps` defaults to 4 for a path against 8 for an outline, and that is a cost decision rather
than a quality one: a step of an outline is a line segment, a step of a path is a whole
`roundConeSpan`, and the sweep's loop is `Unrolled` at the segment count. Worth recording beside
it that `ShapeCost` **undercounts** a sweep badly — `roundConeSpan` is hand-written, so
`GlslWriter` charges one statement per unrolled trip while the driver inlines the whole body, and
flattening multiplies a cost the budget cannot see. The fix belongs to the cost model, not here.

One thing does not follow from flattening, and has to be added: **normals blended across the
segment joints**. The silhouette is smooth at any step count, because it comes from the
geometry; the shading facets are not, and stay visible however fine the tessellation. Blending
each edge's perpendicular with its neighbour's — half a joint's worth on each side, so the two
weigh equally at the shared vertex and the edge's own wins at its middle — is what makes a
tessellated curve read as a curve. It is opt-in, carried in the contour header's second lane,
because a hand-written outline's corners are deliberate and must stay corners. The neighbour is
found **within the contour**, which is what several contours per solid cost this blend.

### Solving the quartic

Ferrari: depress to `u⁴ + p u² + q u + r`, solve the resolvent cubic
`s³ + 2p s² + (p² - 4r) s - q² = 0` for `α² = s`, then factor into
`(u² + αu + β)(u² - αu + γ)` with

```
β = (p + s - q/α) / 2
γ = (p + s + q/α) / 2
```

Take the **largest** real root of the resolvent: its constant term is `-q²`, so its value at
zero is never positive and its largest real root is never negative, which is what makes `α`
real without a special case.

Three things make the difference between this working and not, and none is in the textbook
statement:

- **Never divide by `α`.** `q/α` is the one place the whole solve can lose every digit it has.
  Whenever `q` is near zero the resolvent's root is near zero too, and both closed forms for a
  cubic root end in a subtraction of two numbers the size of the cubic's quadratic
  coefficient — so `s` is what is left after a cancellation, and `α = √s` spreads that noise
  across half its digits. Divide by it and `β` and `γ` are noise. Ferrari's own identities give
  the same number without it: `β + γ = p + s` and `βγ = r`, so

  ```
  (γ - β)² = (β + γ)² - 4βγ = (p + s)² - 4r
  ```

  and the sign of `γ - β` is the sign of `q`, since `α ≥ 0`. Taken this way the split is well
  conditioned exactly where the division is not, is defined at `α == 0`, and makes `βγ == r`
  hold identically — so there is nothing left to check and no fallback to fall back to. Take
  that sign with a comparison rather than with `sign()`, which returns 0 at `q == 0` and would
  put `β` and `γ` both on `p/2`.
- **Newton-polish the resolvent's root.** The cancellation above does not disappear because
  the division did: `s` still sets `β + γ`. Two guarded steps against the cubic as written are
  a few flops on a path that already spends two cube roots, and they put back the digits the
  closed form dropped.
- **Newton-polish the quartic's roots too, but guard it as a refinement.** Ferrari loses
  precision through the resolvent, and a couple of Newton steps against the original polynomial
  recover it. Near a double root the derivative nearly vanishes and an unguarded step jumps
  somewhere unrelated, which downstream is not a slightly wrong surface but an entirely
  invented one. A blob's silhouette is made of near-double roots from end to end. Take the step
  only if it is small, and keep it only if the residual actually falls.

The symptom of getting the first one wrong is worth recording, because it does not look like
an arithmetic problem. `q == 0` on a torus is not a corner case: re-origining the ray at its
closest approach to the ring centre makes `q` proportional to `o.y · rd.y`, so it passes
through zero along a whole band of the image — the band that crosses the ring at its two
horizontal extremes. Answering it with the biquadratic factorisation, which drops `q`, moved
the surface there by a quarter of the tube's radius, and the ring rendered with a dark seam cut
across it at three and nine o'clock that no light direction would move.

## Transforms

Each leaf carries the **inverse** world-to-local matrix, produced on the CPU by composing
the node's ordered `translate` / `rotate` / `scale` modifiers with those of every ancestor,
then inverting once.

```glsl
vec3 lo = (invM * vec4(rayOrigin,    1.0)).xyz;
vec3 ld = (invM * vec4(rayDirection, 0.0)).xyz;   // w = 0: a direction, not a point
```

Two rules that are easy to get wrong:

- **Never renormalise `ld`.** Under a scaling transform the local direction is not unit
  length, and that is precisely what keeps the resulting `t` values on the same scale as
  every other primitive's. Normalising it silently breaks all comparisons between spans of
  differently scaled solids — the symptom is one object always appearing in front of
  another regardless of geometry.
- The normal returns to world space through the **inverse transpose**, which the inverse
  matrix already provides for free:

  ```glsl
  vec3 nWorld = normalize(transpose(mat3(invM)) * nLocal);
  ```

  Using `mat3(invM)` directly instead is correct only for pure rotations and produces
  visibly wrong shading under non-uniform scale.

## GPU representation: a post-order tape

> **Superseded in iteration 12.** Everything from here to the end of "Buffer encoding"
> describes how the scene *used* to reach the GPU: a post-order instruction tape walked by a
> stack machine, and four texture buffers behind it. The scene is now compiled to GLSL — the
> same tree, binarised the same way, emitted as nested calls over named locals — and the arrays
> below are sized per node instead of once for every scene. See
> [code-generation.md](code-generation.md).
>
> It is kept because the algorithm above is unchanged and this section is what explains *why*
> the shape of the encoding was what it was; the span budget arithmetic in particular is still
> exactly how a list's size is computed, only per node rather than per renderer.

GLSL has no recursion, so the shader cannot walk a tree. The CPU therefore flattens the CSG
tree into **post-order (reverse Polish) form** and binarises any n-ary operator into a
left-associated chain. The shader is then a flat loop over a stack machine.

```
difference {                     tape
  box { ... }                      [0]  LEAF   prim=0   (box)
  sphere { ... }        ---->      [1]  LEAF   prim=1   (sphere)
}                                  [2]  DIFF
                                   [3]  END_ROOT

union { a b c }        ---->      LEAF a, LEAF b, UNION, LEAF c, UNION, END_ROOT
```

`END_ROOT` closes one top-level solid. Roots are implicitly unioned, but they are
**resolved one at a time** rather than merged into a single list, and that is a deliberate
choice: it makes the span budget below a *per-root* limit. Merging instead would let a
scene of nine plain spheres overflow a budget that comfortably renders a nine-way CSG tree,
which is precisely backwards.

The one case it gets wrong is a ray that starts inside two overlapping roots at once: the
true union would show where the ray leaves the merged region, and resolving separately
shows whichever it leaves first, which is a surface interior to the union. Put such solids
under an explicit `union` and the case disappears.

There is now a second way to arrive at separate roots, and it is the compiler's doing rather
than the author's: a shape too large for any one program is **cut** into the operands of its
own `union`, which is how `scenes/cube.chroma` renders at all. The cut is what makes the
paragraph above load-bearing rather than a curiosity, and it is why it declines to separate
two *overlapping transmissive* operands, where the difference is a visible seam rather than
an edge case reachable only from inside. See [cutting-unions.md](cutting-unions.md).

Execution:

```glsl
int sp = 0;                                  // span-list stack pointer
for (int i = 0; i < tapeLength; ++i) {
    ivec2 op = fetchInstruction(i);
    if (op.x == OP_LEAF) {
        stack[sp++] = primitiveSpans(op.y, ro, rd);
    } else if (op.x == OP_END_ROOT) {
        resolve(stack[--sp]);                // fold into the answer, then start over
    } else {
        SpanList b = stack[--sp];
        SpanList a = stack[--sp];
        stack[sp++] = combine(op.x, a, b);
    }
}
```

The required stack depth is the **Strahler number** of the binarised tree, not its height:
a balanced tree of 64 leaves needs a depth of 7, and a left-leaning chain of 64 leaves needs
a depth of 2. The CPU computes it during flattening.

### Fixed-size arrays and the span budget

GLSL 3.30 has no dynamically sized arrays, so `MAX_SPANS` and `MAX_STACK` are compile-time
constants in the shader:

| Constant | Value | Meaning |
| --- | --- | --- |
| `MAX_SPANS` | 8 | spans in one list |
| `MAX_STACK` | 4 | span lists held simultaneously |
| `MAX_CROSSINGS` | 32 | crossings a prism or lathe may report, before pairing |
| `MAX_SWEEP_EVENTS` | 24 | depth events a sphere sweep may report |
| `MAX_BLOB_EVENTS` | 16 | component boundaries a blob may report |
| `MAX_LIGHTS` | 8 | lights, passed as uniforms |

**`MAX_SPANS` is a wall, and the other three are not.** This is the single most useful fact
about the shader's shape, and it is not obvious from the source. A span list is multiplied by
`MAX_STACK` and stays live across the whole tape walk; a crossing array is one local array
inside one function. The compiler inlines everything, so both are counted — but not at
remotely the same weight. Measured on a GeForce RTX 4070 SUPER:

| Change | Effect |
| --- | --- |
| `MAX_SPANS` 8 → 9 | −8% sample rate |
| `MAX_SPANS` 9 → 10 | **link fails**: `too many temporaries` |
| `MAX_CROSSINGS` 16 → 32 | no measurable cost |

Past 9 spans the driver refuses the program outright rather than running it slowly. That is
why the span budget below stays at 8 while point-list primitives are free to be tessellated,
and why the caps in `GpuLayout.SpansFor` exist at all.

**That table predates iteration 11 and has not been retaken.** Packing the two surface
references into one int took a quarter off exactly the structure whose size sets this wall, so
the wall has almost certainly moved. It was left where it is deliberately: iteration 11's rule
was speed at an unchanged image, and raising `MAX_SPANS` changes which scenes compile rather
than how fast any of them renders — and it would spend the headroom that bought the 1.7×. What
the budget can now afford is an open question and a good first one for whoever wants to retire
the `SpansFor` clamp below.

The CPU computes the true worst case per subtree while flattening — a union is the sum of its
operands, an intersection is the min, a difference is `|A| + |B|` — and **rejects the scene
with a diagnostic** if it exceeds the budget.

A leaf's own cost is the table in [Primitive spans](#primitive-spans). It was 1 for every
primitive while they were all convex, and each of the four that are not has an exact bound
rather than a generous one:

- a ray crosses each extruded wall of a **prism** at most once, so `edges` crossings pair into
  `edges / 2` spans;
- each band of a **lathe** can be crossed twice, once on the near side of the axis and once on
  the far side, giving `segments` spans;
- a **blob**'s field is a sum of `n` single-humped bumps, which has at most `n` stretches above
  the threshold. A negative component splits one of those in two rather than adding a hump of
  its own, so `n` holds either way;
- a **sphere sweep** of `n` spheres is a union of `n − 1` convex hulls, one span each.

**Those four are then clamped to `MAX_SPANS`, and the clamp is not a proof.** It is the one
place this renderer knowingly departs from "never truncate silently", and the reasoning is
worth stating because the alternative looks safer than it is.

Every one of those bounds counts *segments*. But a curve flattened into segments does not
become a more complicated solid: a vase occupies one or two spans along any ray whether it is
drawn with 6 segments or 60. Holding the exact bound would mean either refusing every
tessellated curve, or a larger `MAX_SPANS` — and `MAX_SPANS` cannot be raised, as measured
above. So the bound is relaxed and the *size* limits are kept strict instead: `MAX_CROSSINGS`,
`MAX_SWEEP_EVENTS` and `MAX_BLOB_EVENTS` are hard array sizes and a shape that would overrun
one is still refused with a diagnostic.

What is given up: an outline convoluted enough to genuinely occupy more than `MAX_SPANS`
stretches of one ray has the extra ones dropped, and renders as a solid with a slice missing.
No shape in `scenes/` comes close, and the failure is at least *visible* rather than subtle —
but it is a real hole in the guarantee, and it was opened deliberately.

The leaf-level overflow check that iteration 6 added is gone with it: a clamped bound cannot
exceed the budget, so the check could never fire. Size limits are enforced in the binders,
which can point at the offending field rather than at the whole solid.
Silently truncating a span list produces geometry that is subtly wrong in a way that looks
like an algorithm bug, which is far more expensive to chase than an explicit error. It
reports the *innermost* offending subtree and only that one: every enclosing operator
overflows as well, and a diagnostic per ancestor buries the one line worth reading.

Because roots are resolved separately, both limits apply to the worst single root rather
than to the scene, so the number of top-level solids is unbounded.

`|B| + 1` intervals are needed to hold `complement(B)` while a difference is being
evaluated. It always fits: every subtree yields at least one span, so
`|B| + 1 <= |A| + |B|`, which is the budget already reserved for the result.

A tape length limit (256 instructions) exists too, but it is a CPU-side sanity cap rather
than an array size — the tape lives in a buffer and the shader simply loops over it.

### Buffer encoding

OpenGL 3.3 Core has no shader storage buffers; those arrived in 4.3. The scene therefore
travels in **four texture buffer objects** (`samplerBuffer`, core since GL 3.1), read with
`texelFetch` — one `vec4` or `ivec4` per texel, indexed by integer, no filtering, no size
limit worth worrying about at this scale. They occupy texture units 0 to 3, and the
accumulation history takes the next one.

`uTape` — `isamplerBuffer`, one texel per instruction:

| Component | Meaning |
| --- | --- |
| `.x` | opcode: `0` leaf, `1` union, `2` intersection, `3` difference, `4` end of root |
| `.y` | primitive index, for `OP_LEAF` only |
| `.z .w` | reserved |

`uPrims` — `samplerBuffer`, **5 texels per primitive**:

| Texel | Contents |
| --- | --- |
| `+0` | `(kind, materialIndex, paramA, paramB)` |
| `+1 .. +4` | the four rows of the inverse world-to-local matrix |

Kind: `0` sphere, `1` box, `2` cylinder, `3` cone, `4` plane, `5` torus, `6` prism, `7` lathe,
`8` blob, `9` sphere sweep, `10` quadric. The two parameter slots hold what the matrix could not
absorb — see [Why some parameters survive the matrix](#why-some-parameters-survive-the-matrix):

| Kind | `paramA` | `paramB` |
| --- | --- | --- |
| sphere, box, cylinder, plane | 0 | 0 |
| cone | cap radius, base being 1 | 0 |
| torus | minor radius, major being 1 | 0 |
| prism, lathe | offset into `uShapes` | total edge count |
| blob | offset into `uShapes` | component count |
| sphere sweep | offset into `uShapes` | sphere count |
| quadric | offset into `uShapes` | 0 |

There used to be one piece of packing here: the lathe's segment count was **negated** to carry
the "blend the normals across joints" flag, because both slots were spoken for and a count is
never zero and never genuinely negative, so its sign was free storage for one bit. Contour ranges
are several numbers rather than one bit, so they forced a header texel, and once there was a
header the flag moved into it. Every count in the table above is now positive.

`uShapes` — `samplerBuffer`, one texel each, only for the kinds whose parameters do not fit:

| Primitive | Layout |
| --- | --- |
| prism, lathe | `(contours, smooth, 0, 0)`, then `(start, count, 0, 0)` per contour, then one texel per edge, `(a.x, a.y, b.x, b.y)` |
| blob | `(threshold, 0, 0, 0)`, then per component `(base.x, base.y, base.z, radius)` and `(cap.x, cap.y, cap.z, strength)` |
| sphere sweep | one texel per sphere, `(c.x, c.y, c.z, radius)` |
| quadric | `(A, B, C, J)`, `(D, E, F, 0)`, `(G, H, I, 0)` |

Edges rather than points for the two contours, though that stores each vertex twice: the
shader's inner loop is over edges, and having both endpoints in one texel makes it one fetch
instead of two plus a wrap-around test on the last iteration. A sweep stores spheres rather
than segments because its path is **open** — there is no wrap-around to avoid, and `n` spheres
are `n − 1` segments. The blob's threshold rides in the buffer because both parameter slots
are already spoken for, and a header texel is cheaper than duplicating one number per
component.

Every blob component is written as a capsule, a **spherical** one having both ends at the same
point. That costs three floats per sphere and buys a shading path with no discriminator in it:
clamping the foot onto a segment of no length gives that point back, so one closest-point
expression serves both kinds.

A scene using none of these leaves this buffer empty.

`uMaterials` — `samplerBuffer`, **4 texels per material**:

| Texel | Contents |
| --- | --- |
| `+0` | `(r, g, b, roughness)` |
| `+1` | `(emissionR, emissionG, emissionB, metallic)` |
| `+2` | `(absorptionR, absorptionG, absorptionB, transmission)` |
| `+3` | `(ior, 0, 0, 0)` |

Every scalar rides in the alpha slot of a colour texel rather than taking one of its own.
The three spare floats of the last texel are left spare on purpose: a scene holds a handful
of materials, so the table's size is worth nothing next to being able to read it. See
[lighting.md](lighting.md#materials-the-metallic-roughness-workflow) and
[transparency.md](transparency.md) for what the fields mean.

Camera, lights and render settings are plain uniforms; they are few, fixed in count per
frame, and change more often than the geometry.

## Shadows

A shadow ray reuses the same tape evaluation. The question is different, though: not "what
is the nearest hit" but "is there *any* span overlapping `(EPS, distanceToLight)`". That
allows an early exit and, importantly, it must **not** apply the "started inside" rule — a
surface should not shadow itself.

Once any material in the scene transmits light, that yes/no answer stops being enough and
the ray has to walk from occluder to occluder gathering a colour — see
[transparency.md](transparency.md#shadow-rays-that-return-a-colour). The renderer keeps both
paths and picks by a flag the compiler sets, so a scene with no transmissive material pays
nothing for the feature. Measured on `cornell.chroma`, the walking version costs about
**5%** of the sample rate.

Offset the shadow ray origin along the surface normal by a small epsilon. Without it, the
surface re-intersects itself at `t ≈ 0` and the image acquires the familiar stippled acne.
Along the *normal*, not along the ray: inside a `difference` cavity the normal points into
the hollow, which is exactly the side the shadow ray has to start on. The bias wants to be
larger than the geometric `EPS` — the hit point carries rounding proportional to `t`, so a
bias sized for `t = 0` stipples the far side of a large scene.

For a point light the ray stops at the light: `maxT = length(lightPos - point)`, or an
occluder standing *behind* the light would cast a shadow. For a directional light it runs
to infinity.

## Numerical notes

- Use a single `EPS` of about `1e-4` in world units, and keep every comparison against it
  consistent. Mixing epsilons between the span merge and the hit selection is a reliable
  source of one-pixel cracks along CSG seams.
- Coplanar faces — subtracting a box whose face lies exactly on another box's face — are
  genuinely ambiguous, and every CSG renderer including POV-Ray produces artefacts there.
  The documented workaround is to make the subtracted solid slightly larger than the
  cut it is meant to produce. This is a property of the model, not a bug to fix.
- `1.0 / 0.0` in GLSL yields `+infinity` and is well defined; the slab test relies on it.
  `0.0 / 0.0` yields `NaN` and is not, which is why the parallel-ray cases in the cylinder
  are branched explicitly rather than left to the arithmetic.
- **Re-origin the ray before forming any polynomial above degree 2.** The coefficients grow
  as a power of the origin's distance while the roots stay near the object, so the whole
  answer lives in the digits a 32-bit float has just thrown away. This is the single largest
  source of wrong pixels in the torus and the blob, and it is one dot product to fix.
- **Count a vertex once.** Where a ray meets two edges of a contour at the same point, an
  inclusive range test reports two crossings — and a duplicate does not merely add a
  zero-width span, it flips the parity of every crossing after it. Half-open ranges, so each
  edge owns its starting vertex and not its ending one, are the fix. Collapsing coincident
  crossings afterwards is *not*: two surfaces meeting at a vertex legitimately produce two
  crossings a hair apart, and merging those breaks the parity it was meant to protect.

## Sources

- T. Roth, *Ray Casting for Modeling Solids*, Computer Graphics and Image Processing, 1982 —
  the original hit-interval / "Roth diagram" formulation.
- [CSG Ray Tracing Revisited: Interactive Rendering of Massive Models](https://www.scitepress.org/papers/2017/61364/61364.pdf) —
  modern restatement of the Roth table and entry/exit classification.
- [POV-Ray documentation, Constructive Solid Geometry](https://www.povray.org/documentation/view/3.7.0/30/) —
  the reference semantics for `union` / `intersection` / `difference` / `merge`.
- [Boolean operations with POV-Ray, Michigan Tech](https://pages.mtu.edu/~shene/COURSES/cs3621/LAB/povray/csg.html) —
  worked examples of each operator.
