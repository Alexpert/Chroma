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

Sphere, box and cylinder are convex, so each produces **at most one span**. Only the
operators can create multi-span lists.

### Why the surface identity is stored and the normal is not

The obvious span layout carries a full surface record at each end: position, normal,
material. That is 8+ floats per endpoint, multiplied by `MAX_SPANS`, multiplied by the
stack depth — far too much register pressure for a fragment shader.

Instead a span stores only the **index of the primitive** that produced each endpoint:

```glsl
struct Span {
    float tIn;
    float tOut;
    int   surfIn;   // encoded primitive reference
    int   surfOut;
};
```

Once the whole tape has been evaluated and the single visible `t` is known, the normal is
recomputed from scratch: fetch that primitive, transform the hit point into its local
space, evaluate its analytic normal, transform back. This happens **once per pixel**, not
once per span.

The encoding packs the flip flag into the sign, and reserves `0` for "no surface":

```
surf = 0                 -> none (used for the ±infinity sentinels of a complement)
surf = +(primIndex + 1)  -> outward normal, as computed by the primitive
surf = -(primIndex + 1)  -> normal must be negated (the surface came from a subtracted operand)
```

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
local space**. This removes all shape parameters from the shader: the only per-primitive
data is a kind, a material index and an inverse matrix.

| Kind | Canonical form |
| --- | --- |
| sphere | unit sphere, centre at the origin, radius 1 |
| box | axis-aligned, `[-1, 1]` on all three axes |
| cylinder | axis along `+Y`, radius 1, from `y = 0` to `y = 1`, capped |

A non-uniform scale on the canonical sphere gives an ellipsoid, and on the canonical
cylinder gives an elliptic cylinder. That falls out for free and is a feature, not an
accident.

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

GLSL has no recursion, so the shader cannot walk a tree. The CPU therefore flattens the CSG
tree into **post-order (reverse Polish) form** and binarises any n-ary operator into a
left-associated chain. The shader is then a flat loop over a stack machine.

```
difference {                     tape
  box { ... }                      [0]  LEAF   prim=0   (box)
  sphere { ... }        ---->      [1]  LEAF   prim=1   (sphere)
}                                  [2]  DIFF

union { a b c }        ---->      LEAF a, LEAF b, UNION, LEAF c, UNION
```

Execution:

```glsl
int sp = 0;                                  // span-list stack pointer
for (int i = 0; i < tapeLength; ++i) {
    ivec2 op = fetchInstruction(i);
    if (op.x == OP_LEAF) {
        stack[sp++] = primitiveSpans(op.y, ro, rd);
    } else {
        SpanList b = stack[--sp];
        SpanList a = stack[--sp];
        stack[sp++] = combine(op.x, a, b);
    }
}
SpanList result = stack[0];
```

The required stack depth is the **Strahler number** of the binarised tree, not its height:
a balanced tree of 64 leaves needs a depth of 7, and a left-leaning chain of 64 leaves needs
a depth of 2. The CPU computes it during flattening.

### Fixed-size arrays and the span budget

GLSL 3.30 has no dynamically sized arrays, so `MAX_SPANS` and `MAX_STACK` are compile-time
constants in the shader:

| Constant | Initial value | Meaning |
| --- | --- | --- |
| `MAX_SPANS` | 8 | spans in one list |
| `MAX_STACK` | 4 | span lists held simultaneously |
| `MAX_TAPE` | 256 | instructions |
| `MAX_LIGHTS` | 8 | lights, passed as uniforms |

The CPU computes the true worst case per subtree while flattening — a convex leaf is 1, a
union is the sum of its operands, an intersection is the min, a difference is
`|A| + |B|` — and **rejects the scene with a diagnostic** if it exceeds the budget.
Silently truncating a span list produces geometry that is subtly wrong in a way that looks
like an algorithm bug, which is far more expensive to chase than an explicit error.

### Buffer encoding

OpenGL 3.3 Core has no shader storage buffers; those arrived in 4.3. The scene therefore
travels in **texture buffer objects** (`samplerBuffer`, core since GL 3.1), read with
`texelFetch` — one `vec4` or `ivec4` per texel, indexed by integer, no filtering, no size
limit worth worrying about at this scale.

`uTape` — `isamplerBuffer`, one texel per instruction:

| Component | Meaning |
| --- | --- |
| `.x` | opcode: `0` leaf, `1` union, `2` intersection, `3` difference |
| `.y` | primitive index, for `OP_LEAF` only |
| `.z .w` | reserved |

`uPrims` — `samplerBuffer`, **5 texels per primitive**:

| Texel | Contents |
| --- | --- |
| `+0` | `(kind, materialIndex, 0, 0)` — kind: `0` sphere, `1` box, `2` cylinder |
| `+1 .. +4` | the four rows of the inverse world-to-local matrix |

`uMaterials` — `samplerBuffer`, 2 texels per material: `(r, g, b, specular)` and
`(shininess, reflectivity, 0, 0)`.

Camera and lights are plain uniforms; they are few, fixed in count per frame, and change
more often than the geometry.

## Shadows

A shadow ray reuses the same tape evaluation. The question is different, though: not "what
is the nearest hit" but "is there *any* span overlapping `(EPS, distanceToLight)`". That
allows an early exit and, importantly, it must **not** apply the "started inside" rule — a
surface should not shadow itself.

Offset the shadow ray origin along the surface normal by a small epsilon. Without it, the
surface re-intersects itself at `t ≈ 0` and the image acquires the familiar stippled acne.

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

## Sources

- T. Roth, *Ray Casting for Modeling Solids*, Computer Graphics and Image Processing, 1982 —
  the original hit-interval / "Roth diagram" formulation.
- [CSG Ray Tracing Revisited: Interactive Rendering of Massive Models](https://www.scitepress.org/papers/2017/61364/61364.pdf) —
  modern restatement of the Roth table and entry/exit classification.
- [POV-Ray documentation, Constructive Solid Geometry](https://www.povray.org/documentation/view/3.7.0/30/) —
  the reference semantics for `union` / `intersection` / `difference` / `merge`.
- [Boolean operations with POV-Ray, Michigan Tech](https://pages.mtu.edu/~shene/COURSES/cs3621/LAB/povray/csg.html) —
  worked examples of each operator.
