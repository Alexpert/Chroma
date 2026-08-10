# The scene description language

The renderer takes one argument: a scene file. This document is the reference for that
file's language. Like [csg-raytracing.md](csg-raytracing.md) it is meant to be
self-sufficient — the POV-Ray material that inspired the design is reproduced in an
appendix so it never has to be looked up again.

> **Status: provisional.** The language deliberately covers only what the renderer can
> currently draw. It will be revised — not merely extended — when loops and macros are
> taken on, since those change what the evaluator has to be. Keeping the syntax layer
> replaceable in one piece is an explicit architectural goal; see
> [architecture.md](architecture.md).

## Shape of the language

The design borrows POV-Ray's idea — a scene *is* a tree of declarative object blocks — but
not its syntax. Where POV-Ray relies on positional arguments and bare juxtaposed keywords,
this language uses a JavaScript-flavoured form that is easier to read, easier to extend
with a new field, and easier to parse without special cases:

- a node is a **type name followed by an object literal**: `sphere { ... }`
- inside a block, `name: value` is a **field** and a bare expression is a **child**
- `let` binds a reusable value, including a whole subtree
- `//` and `/* */` comment, `[x, y, z]` is a vector, arithmetic works on it

File extension: `.chroma`. Encoding: UTF-8. Sample scenes live in [scenes/](../scenes/).

```js
// scenes/csg.chroma

let radius = 1.3;
let red    = material { color: [0.8, 0.2, 0.2], roughness: 0.4 };

render { exposure: 1.3 }

camera {
  position: [0, 2, 5],
  lookAt:   [0, 0, 0],
  fov:      45
}

pointLight       { position: [2, 4, 3], color: [1, 1, 1], intensity: 55, radius: 0.4 }
directionalLight { direction: [-1, -1, -1], color: [0.8, 0.8, 1.1] }

difference {
  box    { min: [-1, -1, -1], max: [1, 1, 1] }
  sphere { center: [0, 0, 0], radius: radius }

  material:  red,
  translate: [0, 0.5, 0]
}
```

## Lexical structure

| Element | Form |
| --- | --- |
| Line comment | `// ... end of line` |
| Block comment | `/* ... */`, not nested |
| Number | `12`, `1.5`, `-0.25`, `1e-3` — always a 64-bit float internally |
| Identifier | `[A-Za-z_][A-Za-z0-9_]*`, case-sensitive, `camelCase` by convention |
| Keyword | `let` — the only reserved word; node names are ordinary identifiers |
| Punctuation | `{ } [ ] ( ) : , ; + - * /` |

Whitespace and newlines are insignificant. **Commas are optional** everywhere they may
appear — between block entries and between vector components — and are consumed and
discarded by the parser. Both of these are the same scene:

```js
sphere { center: [0, 1, 0], radius: 2 }

sphere {
  center: [0 1 0]
  radius: 2
}
```

## Grammar

```ebnf
scene          = statement* ;

statement      = letDecl | expr ;
letDecl        = "let" IDENT "=" expr ";" ;

node           = IDENT objectLiteral ;
objectLiteral  = "{" entry* "}" ;
entry          = field | child ;
field          = IDENT ":" expr [ "," ] ;
child          = expr [ "," ] ;

expr           = additive ;
additive       = multiplicative { ( "+" | "-" ) multiplicative } ;
multiplicative = unary { ( "*" | "/" ) unary } ;
unary          = [ "-" ] primary ;
primary        = NUMBER
               | vector
               | node
               | objectLiteral
               | IDENT
               | "(" expr ")" ;
vector         = "[" [ expr { [ "," ] expr } ] "]" ;
```

Three points about the grammar, since they are what a parser gets wrong:

1. `IDENT` followed by `{` is a node; `IDENT` alone is a reference to a `let` binding. One
   token of lookahead settles it.
2. Inside a block, `IDENT` followed by `:` is a field; anything else starts a child. Two
   tokens of lookahead.
3. A bare `objectLiteral` with no type name is allowed as an expression. Its type is
   inferred from the field receiving it, so `material: { color: [1, 0, 0] }` and
   `material: material { color: [1, 0, 0] }` are the same thing.

**Entry order is preserved.** A block is a list, not a dictionary — the transform modifiers
depend on it (see below), and error messages are better when they can point at the entry as
written.

## Values and operators

Three value types:

| Type | Literal | Notes |
| --- | --- | --- |
| Number | `1.5` | 64-bit float |
| Vector | `[1, 2, 3]` | any length; 3 components serve as both point and colour |
| Object | `sphere { ... }` | a node, typed or anonymous |

**A vector is a flat list of numbers and does not nest.** `[[1, 2], [3, 4]]` is an error, not
a list of pairs. Where a node needs a list of points — `prism` and `lathe` — the components
are interleaved instead, `[x0, z0, x1, z1, ...]`, and the node pairs them up. Widening the
value model would be the better answer and is a change to the language rather than to a node.

Arithmetic applies to numbers and vectors, component-wise, with scalar promotion. Objects
support no operators.

| Precedence | Operators | Associativity |
| --- | --- | --- |
| 1 (highest) | unary `-` | right |
| 2 | `*` `/` | left |
| 3 | `+` `-` | left |

```js
[1, 2, 3] * 2        // [2, 4, 6]
[1, 2, 3] + [0, 1, 0] // [1, 3, 3]
-[1, 0, 0]           // [-1, 0, 0]
2 * radius + 0.5     // number
```

Mixing lengths (`[1, 2] + [1, 2, 3]`) is an error. Multiplying two vectors is component-wise,
not a dot or cross product; those are not available yet.

### `let` bindings

```js
let radius = 1.3;
let unit   = sphere { center: [0, 0, 0], radius: 1 };
```

Bindings are file-scoped, visible from the point of declaration onward, and immutable.
Redeclaring a name is an error rather than a shadow — silent shadowing in a scene file is
almost always a typo.

A binding may hold a whole subtree. Referencing it twice **instantiates it twice**, and the
resulting solids are independent. A reference on its own takes no modifiers — there is no
`unit { translate: ... }` form, since that would read as a node type called `unit`. To place
a copy, wrap the reference in a `union`, which is a solid like any other and accepts the
usual modifiers:

```js
let unit = sphere { radius: 1 };

union { unit, translate: [-2, 0, 0] }
union { unit, translate: [ 2, 0, 0 ] }
```

## Node reference

### `camera` — required, exactly one

| Field | Type | Default | Meaning |
| --- | --- | --- | --- |
| `position` | vec3 | — | eye position, world space |
| `lookAt` | vec3 | `[0,0,0]` | point the camera aims at |
| `up` | vec3 | `[0,1,0]` | roll reference; must not be parallel to the view direction |
| `fov` | number | `45` | **vertical** field of view, degrees |

### `render` — optional, at most one

Settings that belong to the scene rather than to the build. Absent means the defaults.

| Field | Type | Default | Meaning |
| --- | --- | --- | --- |
| `maxBounces` | integer | `4` | path length; `1` is direct lighting only. Must be `1..16` |
| `exposure` | number | `1` | multiplier applied before tone mapping |

`maxBounces` outside its range, or written with a fraction, is an **error** rather than a
clamp: the loop runs per pixel per frame, so an absurd depth is a typing mistake and a
frozen driver costs far more to diagnose than a message.

### `pointLight`

| Field | Type | Default | Meaning |
| --- | --- | --- | --- |
| `position` | vec3 | — | |
| `color` | vec3 | `[1,1,1]` | |
| `intensity` | number | `1` | see the falloff note below |
| `radius` | number | `0` | `0` is an idealised point and gives hard shadows; above `0` the light is a sphere and its shadows gain a penumbra |

**Brightness falls off with the square of the distance.** A light 5 units away delivers
1/25 of what the same `intensity` delivers at 1 unit, so useful values are much larger than
they look — a room-sized scene typically wants tens.

`radius` is a **pure softness control**: the light's radiance is normalised so that widening
it does not change how brightly anything is lit. Only the shadows change, and the penumbra
widens with the distance between the occluder and the surface, as it does in life.

### `directionalLight`

| Field | Type | Default | Meaning |
| --- | --- | --- | --- |
| `direction` | vec3 | — | direction the light travels *towards*, normalised on load |
| `color` | vec3 | `[1,1,1]` | |
| `intensity` | number | `1` | infinitely far away, so no falloff |

### `material`

The metallic-roughness model. See [lighting.md](lighting.md) for the BRDF these drive.

| Field | Type | Default | Meaning |
| --- | --- | --- | --- |
| `color` | vec3 | `[0.8,0.8,0.8]` | base colour, linear, `0..1` — diffuse albedo for a dielectric, reflectance tint for a metal |
| `roughness` | number | `0.5` | `0` mirror-smooth, `1` fully matte. Clamped |
| `metallic` | number | `0` | `0` dielectric, `1` metal. Clamped |
| `emission` | vec3 | `[0,0,0]` | radiance emitted; **not** clamped, since a light is not limited to 1 |
| `transmission` | number | `0` | how much of the non-reflected light passes through instead of scattering. `0` opaque, `1` clear glass. Clamped |
| `ior` | number | `1.5` | index of refraction. Must be `1` or more |
| `absorption` | vec3 | `[0,0,0]` | Beer–Lambert extinction **per world unit**, inside the solid. Must not be negative |

A mirror is `metallic: 1, roughness: 0`. Note that a metal has **no diffuse component at
all** — it only reflects its surroundings, so a metal solid in an otherwise empty scene
renders nearly black. That is correct, not broken; give it something to reflect.

Glass is `transmission: 1, roughness: 0`. Three things about the transmissive fields are
worth knowing before the first attempt:

- **`color` does nothing at `transmission: 1`.** What is not reflected goes through rather
  than scattering, so there is no diffuse lobe left to tint. To tint glass, use `absorption`.
- **`absorption` is a rate, not a multiplier.** Doubling a solid's thickness *squares* the
  transmittance. That is why the same material looks pale in a thin slab and deep in a thick
  one, which is exactly how real glass behaves.
- **`ior` also sets reflectance.** A dielectric reflects `((ior − 1)/(ior + 1))²` head-on,
  which at the default 1.5 is the 0.04 the renderer used as a constant before this field
  existed. `roughness` still applies to both the reflected and the transmitted lobe, so a
  high `roughness` with `transmission: 1` gives frosted glass.

`transmission` is ignored on a metal — a metal does not transmit — and setting both is
reported as a warning rather than an error, because the picture is right and only the
expectation is wrong.

See [transparency.md](transparency.md) for the model, and its
[Limits](transparency.md#limits-of-this-implementation) section for what it does not do:
notably no nested media, no dispersion, and no subsurface scattering.

An emissive solid is *seen* rather than used to light the scene. It is found only when a
bounced ray happens to hit it, so a large emissive surface converges well and a small one
stays noisy — use `pointLight { radius }` to illuminate, and `emission` to make the source
visible. [lighting.md](lighting.md#emissive-surfaces-are-not-sampled) explains why.

### Primitives

| Node | Field | Type | Default |
| --- | --- | --- | --- |
| `sphere` | `center` | vec3 | `[0,0,0]` |
| | `radius` | number | `1` |
| `box` | `min` | vec3 | `[-1,-1,-1]` |
| | `max` | vec3 | `[1,1,1]` |
| `cylinder` | `base` | vec3 | `[0,0,0]` |
| | `cap` | vec3 | `[0,1,0]` |
| | `radius` | number | `1` |
| `cone` | `base` | vec3 | `[0,0,0]` |
| | `baseRadius` | number | `1` |
| | `cap` | vec3 | `[0,1,0]` |
| | `capRadius` | number | `0` |
| `plane` | `normal` | vec3 | `[0,1,0]` |
| | `distance` | number | `0` |
| `torus` | `center` | vec3 | `[0,0,0]` |
| | `majorRadius` | number | `1` |
| | `minorRadius` | number | `0.25` |
| `prism` | `points` | vec2n | — |
| | `bottom` | number | `0` |
| | `top` | number | `1` |
| `lathe` | `points` | vec2n | — |
| `blob` | `threshold` | number | `1` |
| | children | `blobSphere` | at least one |
| `blobSphere` | `center` | vec3 | `[0,0,0]` |
| | `radius` | number | `1` |
| | `strength` | number | `1` |

**Every primitive is a solid**, with an inside and an outside, and every one of them is a
legal operand of `union`, `intersection` and `difference`. None of them has POV-Ray's `open`
modifier, and none will: an uncapped shape has no well-defined inside, and CSG needs one.

`box` is axis-aligned *as written*; rotate it with the `rotate` modifier. `cylinder` is
capped at both ends — it is a solid, not a tube, which is what CSG requires.

`cone` is a truncated cone, capped at both ends. `capRadius: 0` — the default — gives the
familiar pointed cone; equal radii give a cylinder. Writing the narrow end as the `base` is
fine and describes the same solid.

`plane` is an **infinite half-space**: everything on the side its `normal` points away from.
`distance` is measured along the normal, which is normalised on load, so `plane { normal: [0,
2, 0], distance: 3 }` and `plane { normal: [0, 1, 0], distance: 3 }` are the same plane.
Being a solid rather than a surface is what makes `difference { plane, sphere }` a ground
with a crater in it. It is the natural ground for a scene, and the only primitive whose span
runs to infinity.

`torus` lies in the XZ plane with Y through its hole, as POV-Ray's does. It is the first
primitive here that is **not convex** — a ray through the hole and out the far side crosses
it twice. `minorRadius` must be smaller than `majorRadius`: POV-Ray's self-intersecting
spindle torus offers four different answers to what its inside is, and none of them is a
shape a CSG operand can be relied on to be, so it is refused rather than guessed at.

#### Point lists

`prism` and `lathe` take a **flat list of interleaved pairs**, because the language's vectors
are flat lists of numbers and cannot nest:

```js
prism { points: [x0, z0,  x1, z1,  x2, z2, ...] }   // a contour in the XZ plane
lathe { points: [r0, y0,  r1, y1,  r2, y2, ...] }   // an outline in (radius, y)
```

At least three points, and **the contour closes implicitly** — the last point joins back to
the first. Repeating the first point at the end, which is how POV-Ray closes a linear spline,
is accepted and ignored.

`prism` sweeps its contour along Y between `bottom` and `top`, and caps both ends. `lathe`
revolves its outline about the Y axis; no point may have a negative radius, since the surface
of revolution of a curve that crosses the axis does not bound a solid.

Two restrictions, both deliberate:

- **Linear segments only.** POV-Ray offers quadratic, cubic and Bézier splines. Those are a
  CPU-side tessellation into segments and change nothing in the shader; they are simply not
  built yet.
- **One contour per solid.** POV-Ray's `prism` accepts several and fills them even-odd, which
  is how a hole is punched into one. That mechanism exists because POV-Ray's prism is not a
  CSG shape; here it is, so write the hole as a `difference`.

#### `blob`

A `blob` is the surface where a **sum of spherical fields** reaches `threshold`. Each
`blobSphere` child contributes `strength · (1 − (d/radius)²)²` out to its own `radius`, and
nothing beyond it.

```js
blob {
  threshold: 0.6,

  blobSphere { center: [-0.5, 0, 0], radius: 1.1 }
  blobSphere { center: [ 0.5, 0, 0], radius: 1.1 }
}
```

Two components that overlap **merge into one smooth surface** rather than showing a seam,
because the surface belongs to the sum and not to either sphere. That is the whole point of a
blob, and it is not something `union` can do.

A negative `strength` hollows the blob out where it overlaps a positive one instead of adding
to it. `threshold` must be above 0, and a component's `radius` must be too. Cylindrical
components, which POV-Ray also offers, are not built: their field is piecewise in a way the
spherical one is not, and each piece would need its own solve.

Note that `radius` is the reach of the *field*, not the size of the result. A lone component
of `radius: 1.1` and `strength: 1` at `threshold: 0.55` produces a sphere of radius 0.56 — the
surface always sits well inside the component that made it, and raising `threshold` pulls it
further in.

#### How much of the span budget each one costs

The shader holds a fixed number of spans per ray (see
[csg-raytracing.md](csg-raytracing.md#fixed-size-arrays-and-the-span-budget)), and a scene
that would exceed it is refused with a diagnostic rather than drawn wrong. Until these
primitives existed every one of them was convex and cost exactly 1, so this only became worth
knowing now:

| Primitive | Spans |
| --- | --- |
| `sphere`, `box`, `cylinder`, `cone`, `plane` | 1 |
| `torus` | 2 |
| `prism` | points ÷ 2 |
| `lathe` | points |
| `blob` | components |

With the current budget of 8 that allows a 16-point prism, an 8-point lathe or an 8-component
blob **standing alone**; combining one with anything else in a CSG operator leaves less. The
error names the solid and the number it needed.

### Operators

`union`, `intersection` and `difference` take their operands as **children**, not as a
field:

```js
union {
  sphere { center: [-1, 0, 0], radius: 1 }
  sphere { center: [ 1, 0, 0], radius: 1 }
}
```

| Node | Operands | Semantics |
| --- | --- | --- |
| `union` | 1..n | everything inside any operand |
| `intersection` | 2..n | only what is inside every operand |
| `difference` | 2..n | the **first** operand minus all the others |

`difference` is order-sensitive; the others are not. All three accept the shared modifiers
below, so an operator behaves exactly like a primitive to its own parent.

**There is no `merge` operator, and none is needed.** POV-Ray has one because its `union` is
a shortcut that keeps every operand's surfaces, so two overlapping transparent solids show
the faces buried inside each other. `union` here merges intervals, and those interior faces
stop existing — see
[csg-raytracing.md](csg-raytracing.md#union--a--b). A `merge` keyword would be a second name
for `union`.

### Top-level solids are unioned, but not merged

Objects declared at the top level of the file are implicitly unioned — but by a different
mechanism, and for transparent solids the difference is visible.

The renderer resolves each top-level solid **separately**, which is what keeps the span
budget a per-root limit (a scene may hold any number of solids however tight the budget is).
Separate resolution means their spans are never combined, so two *overlapping transparent*
top-level solids **do** show the interior faces where they cross. An explicit `union` merges
them and the faces vanish.

```js
// A visible lens-shaped seam where they cross.
sphere { center: [-0.45, 0, 0], radius: 0.9, material: glass }
sphere { center: [ 0.45, 0, 0], radius: 0.9, material: glass }

// No seam: one solid.
union {
  sphere { center: [-0.45, 0, 0], radius: 0.9 }
  sphere { center: [ 0.45, 0, 0], radius: 0.9 }
  material: glass
}
```

For opaque solids the two are indistinguishable, which is why this only surfaced once
transparency existed. **If two overlapping glass solids are meant to be one object, write
the `union`.**

## Shared modifiers

Every solid — primitive or operator — accepts these fields.

| Field | Type | Effect |
| --- | --- | --- |
| `material` | material | applies to this solid and, by inheritance, to descendants that declare none |
| `translate` | vec3 | translation |
| `rotate` | vec3 | Euler angles in **degrees**, applied X then Y then Z |
| `scale` | vec3 or number | a number scales uniformly |

**Transform modifiers apply in the order they are written**, and that order matters:

```js
sphere { radius: 1, translate: [2, 0, 0], rotate: [0, 90, 0] }   // orbits to [0, 0, -2]
sphere { radius: 1, rotate: [0, 90, 0], translate: [2, 0, 0] }   // stays at [2, 0, 0]
```

A parent's transform composes on top of its children's — a child is placed in its parent's
space, as in any scene graph.

## Coordinate system

Right-handed, `+X` right, `+Y` up, `+Z` towards the viewer. Rotations are counter-clockwise
when looking down the positive axis towards the origin. Angles are in degrees everywhere in
the language and converted to radians on load.

**Put the camera at positive Z.** With `position: [0, 0, 7]` and `lookAt: [0, 0, 0]` the
camera looks down `-Z`, and world `+X` appears on the right of the image, which is what
everyone expects.

Placing it at *negative* Z instead is legal and points the camera down `+Z` — and then world
`+X` appears on the **left**, because that is what looking the other way along an axis
means. The scene is not broken, it is mirrored, and the symptom is a layout that reads
backwards with no error to explain it.

This is the trap POV-Ray habits walk into: POV-Ray is left-handed by default and its
scenes conventionally sit at `location <0, 2, -5>`. Transliterating that literally mirrors
the result. Negate the Z of the camera and of every light when porting a scene.

## Migrating a scene written before iteration 4

Iteration 4 replaced Blinn-Phong with an energy-conserving BRDF, which changed the material
fields and the meaning of `intensity`. Four mechanical substitutions:

| Before | Now |
| --- | --- |
| `specular: s`, `shininess: n` | `roughness: r`, with `r` near `0` for a tight highlight and near `1` for a matte surface |
| `reflectivity: 1` | `metallic: 1, roughness: 0` |
| `pointLight { intensity: i }` | multiply `i` by roughly the **square of the distance** to what it lights |
| `directionalLight { intensity: i }` | multiply `i` by about `3` (the `1/π` a Lambert surface now applies) |

A scene that still uses the old field names is reported field by field — they are not
silently ignored.

**Iteration 5 needs no migration at all.** It added `transmission`, `ior` and `absorption`,
and their defaults are neutral by construction: `transmission: 0` is the opaque material
that already existed, and `ior: 1.5` reproduces the 0.04 reflectance that was previously a
constant. Every scene written before it renders identically — measured, not assumed.

**Nor does iteration 6.** It only adds node types — `cone`, `plane`, `torus`, `prism`,
`lathe`, `blob` and `blobSphere` — and changes nothing about the seven that were already
there. The one thing to know is that `torus` used to be an *unknown* node name, so a file
that misspelled its way into one now gets a different error.

## Errors

Diagnostics carry a file, a line and a column, are accumulated rather than thrown one at a
time, and the parser resynchronises on `}` so a single run reports as many problems as it
can find:

```
scenes/demo.chroma:12:11: error: unknown field 'raduis' on 'sphere'
scenes/demo.chroma:19:3:  error: 'difference' needs at least 2 operands, found 1
scenes/demo.chroma:24:14: error: expected a vector of 3 components, found 2
```

Any error means no render: the process prints every diagnostic and exits non-zero.

---

## Appendix — POV-Ray syntax, for reference

Recorded here so the POV-Ray documentation does not have to be consulted again. This is
**not** what the renderer accepts; it is the prior art the design was measured against, and
the reference for the CSG *semantics*, which are copied faithfully even though the syntax is
not.

### General form

POV-Ray blocks are `keyword { positional args, then bare modifiers }`. Vectors use angle
brackets, `x` `y` `z` are the unit vectors, and a lone float promotes to a vector, so
`sphere { 0, 1 }` is a unit sphere at the origin.

```pov
#declare Radius = 1.3;            // #local for block scope

camera {
  location <0, 2, -5>
  look_at  <0, 0, 0>
  angle    45                     // HORIZONTAL fov, unlike ours
}

light_source { <2, 4, -3> color rgb <1, 1, 1> }
light_source { <2, 4, -3> color rgb 1 parallel point_at <0, 0, 0> }   // directional

difference {
  box    { <-1, -1, -1>, <1, 1, 1> }
  sphere { <0, 0, 0>, Radius }
  pigment { color rgb <0.8, 0.2, 0.2> }
  finish  { ambient 0.1 diffuse 0.7 specular 0.4 reflection 0.2 }
  translate y*0.5
}
```

### Primitives

| POV-Ray | Arguments |
| --- | --- |
| `sphere { <c>, r }` | centre, radius |
| `box { <c1>, <c2> }` | two opposite corners, axis-aligned before transforms |
| `cylinder { <base>, <cap>, r [open] }` | two end points, radius; `open` removes the caps |
| `cone { <base>, r1, <cap>, r2 [open] }` | truncated cone |
| `plane { <normal>, dist }` | an infinite half-space — a solid, usable in CSG |
| `torus { major, minor [SPINDLE_MODE] }` | lies in the XZ plane; the spindle mode picks an inside when `minor >= major` |
| `prism { [SPLINE] [SWEEP] h1, h2, n, <p1>, ... [open] }` | contours in the XZ plane swept along Y; several contours fill even-odd |
| `lathe { [SPLINE] n, <p1>, ... }` | an outline in `<radius, y>` revolved about Y |
| `blob { threshold t, sphere { <c>, r, strength s } ... }` | isosurface of a sum of fields; components may also be `cylinder { <e1>, <e2>, r, strength s }` |

`SPLINE` is `linear_spline` (the default), `quadratic_spline`, `cubic_spline` or
`bezier_spline`; `SWEEP` is `linear_sweep` (the default) or `conic_sweep`, which tapers the
contour as it rises. A linear spline is closed by repeating its first point at the end. Most
of these carry an optional `sturm` flag, selecting a slower but more accurate root solver for
the higher-degree surfaces.

Four differences from what this renderer accepts, and why:

- **No `open`.** POV-Ray lets a cone, cylinder or prism lose its caps. The result has no
  well-defined inside, so it cannot be a CSG operand — which every solid here has to be.
- **Only linear splines**, and only one contour per `prism` or `lathe`. The curved splines
  are a tessellation and are not built yet; the multi-contour rule exists in POV-Ray to punch
  holes into a shape that is not otherwise CSG-capable, which is not a problem here.
- **No spindle torus**, and so no spindle mode to choose between.
- **Spherical blob components only.** A cylindrical component's field is piecewise in a way
  the spherical one is not.

### CSG

| POV-Ray | Semantics |
| --- | --- |
| `union { A B ... }` | everything inside any operand |
| `intersection { A B ... }` | only what is inside every operand |
| `difference { A B ... }` | `A` minus every subsequent operand |
| `merge { A B ... }` | union that also removes the internal surfaces — only distinguishable on transparent objects |
| `inverse` | modifier flipping a solid's inside and outside |

`merge` and `inverse` have no equivalent here yet; `merge` is a rendering optimisation that
only matters with transparency, and `inverse` is expressible as `difference` from a large
enclosing solid.

### Modifiers and directives

- Transforms: `translate <v>`, `rotate <deg, deg, deg>`, `scale <v>`, `matrix <...>` —
  applied in written order, same rule as here.
- Appearance: `texture { pigment { color rgb <r,g,b> } finish { ... } }`; `pigment` and
  `finish` may also appear bare in the object block.
- Directives: `#declare` / `#local`, `#include "colors.inc"`, `#macro` .. `#end`,
  `#while` .. `#end`, `#if` / `#else` / `#end`, `#debug`.
- Comments are `//` and `/* */`, same as here.

The directive family is the part worth revisiting when loops and macros come up: POV-Ray
puts them in a separate `#`-prefixed preprocessor layer that runs before parsing, which is
a design decision to weigh rather than copy.

### Sources

- [POV-Ray reference, scene description language](https://www.povray.org/documentation/3.7.0/r3_3.html)
- [POV-Ray reference, CSG](https://www.povray.org/documentation/view/3.7.0/30/)
- [POV-Wiki, Reference:Scene Description Language](https://wiki.povray.org/content/Reference:Scene_Description_Language)
- [Boolean operations with POV-Ray, Michigan Tech](https://pages.mtu.edu/~shene/COURSES/cs3621/LAB/povray/csg.html)
