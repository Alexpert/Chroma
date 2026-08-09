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

File extension: `.chroma`. Encoding: UTF-8. Sample scenes will live in `scenes/` at the
repository root, added in iteration 1.

```js
// scenes/csg.chroma

let radius = 1.3;
let red    = material { color: [0.8, 0.2, 0.2], specular: 0.4, shininess: 32 };

camera {
  position: [0, 2, -5],
  lookAt:   [0, 0, 0],
  fov:      45
}

pointLight       { position: [2, 4, -3], color: [1, 1, 1], intensity: 1.0 }
directionalLight { direction: [-1, -1, 1], color: [0.25, 0.25, 0.35] }

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

A binding may hold a whole subtree. Referencing it twice **instantiates it twice**; the
solids are independent, and each may carry its own transform at the use site.

## Node reference

### `camera` — required, exactly one

| Field | Type | Default | Meaning |
| --- | --- | --- | --- |
| `position` | vec3 | — | eye position, world space |
| `lookAt` | vec3 | `[0,0,0]` | point the camera aims at |
| `up` | vec3 | `[0,1,0]` | roll reference; must not be parallel to the view direction |
| `fov` | number | `45` | **vertical** field of view, degrees |

### `pointLight`

| Field | Type | Default |
| --- | --- | --- |
| `position` | vec3 | — |
| `color` | vec3 | `[1,1,1]` |
| `intensity` | number | `1` |

### `directionalLight`

| Field | Type | Default | Meaning |
| --- | --- | --- | --- |
| `direction` | vec3 | — | direction the light travels *towards*, normalised on load |
| `color` | vec3 | `[1,1,1]` | |
| `intensity` | number | `1` | |

### `material`

| Field | Type | Default | Meaning |
| --- | --- | --- | --- |
| `color` | vec3 | `[0.8,0.8,0.8]` | diffuse albedo, linear, `0..1` |
| `specular` | number | `0` | Blinn-Phong specular weight |
| `shininess` | number | `32` | Blinn-Phong exponent |
| `reflectivity` | number | `0` | reserved; ignored until reflections exist |

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

`box` is axis-aligned *as written*; rotate it with the `rotate` modifier. `cylinder` is
capped at both ends — it is a solid, not a tube, which is what CSG requires.

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

Objects declared at the top level of the file are implicitly unioned.

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

Right-handed, `+X` right, `+Y` up, `+Z` towards the viewer; the camera looks down `-Z` when
`position` is `[0, 0, d]` and `lookAt` is the origin. Rotations are counter-clockwise when
looking down the positive axis towards the origin. Angles are in degrees everywhere in the
language and converted to radians on load.

Note this is the opposite handedness from POV-Ray, which is left-handed by default. A
POV-Ray scene transliterated by hand will come out mirrored in Z.

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
| `torus { major, minor }` | lies in the XZ plane |

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
