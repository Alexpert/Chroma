# Transparency: refraction, Fresnel, absorption and caustics

This document is meant to be **self-sufficient**: everything needed to implement the
transmissive side is here, so no further web research should be required. Sources are listed
at the end for provenance only.

It is the third of the renderer's reference documents:

- [csg-raytracing.md](csg-raytracing.md) — finding the surface.
- [lighting.md](lighting.md) — what happens at an **opaque** surface.
- this one — what happens when the surface is a **boundary between two media** instead of the
  end of the road.

The last section, [Limits](#limits-of-this-implementation), states plainly what this renderer
does not do. It is not an appendix: knowing which wrong images are expected is what separates
a limitation from a bug.

## What changes, and why it has to

Up to iteration 4 a ray that hit a surface either scattered off it or died there. Every solid
was opaque, and `sampleBrdf` only ever returned directions in the hemisphere **above** the
surface.

Transmission breaks that assumption in three places at once:

1. The outgoing direction can be **below** the surface.
2. The ray then travels **through matter**, which absorbs it — so the segment between two
   hits stops being free.
3. A shadow ray's answer stops being yes/no and becomes **a colour**.

The interval algorithm pays off a second time here, and it is worth being precise about how,
because it is easy to overclaim. What comes free is **the side of the surface the ray is
on**: `resolveRoot` already distinguishes a hit at `tIn` (entering) from a hit at `tOut`
(leaving), so the renderer never has to guess it from the orientation of a normal. A mesh
renderer does have to guess, and it gets it wrong the moment a mesh is not closed.

What does **not** come free is the distance travelled inside. A span is `[tIn, tOut]`, but
the ray *bends* at the boundary, so it leaves somewhere other than the original `tOut` and
has to be traced again. The distance is simply the `t` of the next hit — cheap, but not
"already computed".

## Snell's law

At a boundary between media of refractive index `η_i` (incident side) and `η_t`
(transmitted side):

```
η_i · sin θ_i = η_t · sin θ_t
```

Everything below uses the **relative** index `η = η_i / η_t`, so that:

```
sin θ_t = η · sin θ_i
cos²θ_t = 1 − η² · (1 − cos²θ_i)
```

Entering glass from vacuum, `η = 1/1.5`. Leaving it, `η = 1.5`. That single number is the
only thing that changes between the two directions of travel, which is why the code needs no
separate "entering" and "leaving" branches.

### Total internal reflection

When `cos²θ_t` comes out **negative**, there is no transmitted direction: the light is
entirely reflected. This is not a numerical accident to be clamped away — it is a physical
regime, and it is what makes the underside of a water surface look like a mirror.

It can only happen when `η > 1`, that is, going from the dense medium to the thin one. The
critical angle for glass is about 41.8°.

```glsl
float sinT2 = eta * eta * (1.0 - cosThetaI * cosThetaI);
if (sinT2 > 1.0) { /* total internal reflection: F = 1, no transmitted ray */ }
```

GLSL's built-in `refract(I, N, eta)` returns the **zero vector** in this case rather than
signalling an error, so a caller that does not test for it will happily trace a ray with no
direction. The symptom is a black ring on the silhouette of a glass sphere, exactly where
the geometry is most interesting.

## Fresnel for a dielectric

How much of the light reflects rather than transmits, as a function of angle. At normal
incidence:

```
F0 = ((η − 1) / (η + 1))²
```

This expression is **symmetric** under `η → 1/η`, so a boundary has the same `F0` whichever
way it is crossed. For `ior = 1.5` it gives **0.04** — which is exactly the constant
[lighting.md](lighting.md#materials-the-metallic-roughness-workflow) hard-coded for
dielectrics. The two models were never in conflict; iteration 4 simply had no `ior` field to
derive it from. Deriving it now unifies them, and it is why `ior: 1.5` is the default: every
scene written before this iteration keeps its exact appearance.

Schlick's approximation then gives the angular dependence:

```
F(θ) = F0 + (1 − F0) · (1 − cos θ)⁵
```

### The trap: Schlick seen from the dense side

Schlick's approximation is derived for light arriving from the **thinner** medium. Used
unchanged from inside the glass it is simply wrong: it reaches `F = 1` only at 90°, whereas
the real reflectance reaches 1 at the critical angle, 41.8° for glass.

The fix is one line — **use the transmitted angle when `η > 1`**:

```glsl
float cosine = eta > 1.0 ? cosThetaT : cosThetaI;
return f0 + (1.0 - f0) * pow(1.0 - cosine, 5.0);
```

With `cos θ_t`, the term reaches 1 precisely when `θ_t` reaches 90°, which is the critical
angle by definition. The two cases then agree at the boundary instead of contradicting each
other.

Get this wrong and the image still looks like glass — it just has a dark rim where the
reflectance dropped when it should have risen. That is the failure mode this whole document
is written to avoid: **plausible and wrong** is far more expensive than obviously broken.

## The microfacet BTDF

The reflection lobe of [lighting.md](lighting.md#the-brdf-lambert--cook-torrance-ggx) models
the surface as a field of tiny mirrors whose normals follow the GGX distribution `D`. The
transmission lobe uses the same field, with the mirrors replaced by tiny **refracting**
facets. This is Walter et al. 2007, and it is what makes `roughness` mean the same thing on
both sides of the surface: frosted glass is exactly polished glass with a wider `D`.

### The refraction half-vector

For reflection, the microfacet that turns `v` into `l` is the ordinary half-vector, and it
does not depend on the medium:

```
h_r = normalize(v + l)
```

For refraction it does, because Snell's law weights the two directions by their indices:

```
h_t = −normalize(η_i · v + η_t · l)
```

The minus sign puts `h_t` back on the same side as the surface normal. Note that at `η_i =
η_t` this degenerates to `−normalize(v + l)`, which points backwards — consistent with the
fact that at equal indices there is no bending at all and "refraction" is just going
straight on.

### The BTDF itself

```
                |v·h| · |l·h|      η_t² · (1 − F) · G · D
f_t(v, l)  =  ───────────────── · ────────────────────────
                |n·v| · |n·l|      (η_i·(v·h) + η_t·(l·h))²
```

Compare it with the reflection lobe, `D·G·F / (4·(n·v)·(n·l))`: same `D`, same `G`, `F`
replaced by `1 − F`, and the constant `4` replaced by a denominator that depends on the two
indices. That denominator is the whole difference between reflecting and refracting.

### The simplification that makes it implementable

Written out, the BTDF is intimidating. Sampled properly, almost all of it cancels.

Draw the microfacet normal `h` from the distribution `D`, as the reflection lobe already
does. Its density on the hemisphere is `p_h = D(h)·|n·h|`. Converting that to a density over
the outgoing direction requires the Jacobian of the half-vector transform, which for
refraction is (Walter eq. 17):

```
|∂ω_h / ∂ω_l| = η_t² · |l·h| / (η_i·(v·h) + η_t·(l·h))²
```

So `pdf(l) = D(h)·|n·h| · η_t²·|l·h| / Den²`, with `Den` the same denominator as above. The
Monte Carlo weight is `f_t · |n·l| / pdf(l)`, and when the two are divided:

- `D` cancels,
- `η_t²` cancels,
- `Den²` cancels,
- `|l·h|` cancels,
- `|n·l|` cancels.

What survives is:

```
weight_transmission = (1 − F) · G · |v·h| / (|n·v| · |n·h|)
```

which is the **same expression** as the reflection weight with `F` replaced by `1 − F` — and
the reflection weight is already in the shader, from iteration 4:

```
weight_reflection   =      F  · G · |v·h| / (|n·v| · |n·h|)
```

**This is the review test for the whole section.** If `D`, `η`, or the `Den²` denominator
still appears anywhere in the sampling weight, the derivation was not carried through and the
image will be wrong in a way that still looks like glass.

### The η² factor, and why it is absent

Radiance is compressed when light enters a denser medium: a beam narrows by `η²`. A renderer
that transports radiance from the camera has to account for it, and different texts put the
factor in different places, which is a reliable source of confusion.

Here it cancels twice over. It cancels once inside the weight, as shown above. And any path
that enters a solid must leave it again — the camera is in vacuum — so the `η²` on the way
in and the `1/η²` on the way out multiply to 1 over the whole traversal. There is no factor
to write, and the absence is deliberate rather than forgotten.

This stops being true for a camera *inside* a medium; see [Limits](#limits-of-this-implementation).

## Three lobes

An opaque surface splits incoming light two ways: specular reflection, and diffuse. A
transmissive one splits it three ways. The material's `transmission` field says how the
**non-reflected** part is divided:

```
BSDF  =  specular reflection
      +  transmission       ·  specular transmission
      +  (1 − transmission) ·  diffuse
```

`transmission: 0` removes the middle term and leaves exactly the iteration-4 material.
`transmission: 1` removes the diffuse term and gives clear glass. In between is a
translucent plastic.

### Sampling the three

```
h = sampleGgxHalf(n, alpha, u)               // one microfacet, drawn from D

if v·h > 0:                                  // the facet is visible
    F        = fresnel(v·h, eta)             // 1 under total internal reflection
    pReflect = clamp(luminance(F), 0.05, 0.95)
else:
    F = 0 ;  pReflect = 0                    // no specular lobe exists through it

with probability pReflect         →  l = reflect(−v, h)
otherwise, sub-divide (1 − pReflect):
    with probability transmission →  l = refract(−v, h, eta), or end the path
                                       if the facet was not visible
    otherwise                     →  l = cosineHemisphere(n, u)
```

**The visibility test must not end the path.** A microfacet turned away from the viewer
carries neither specular lobe — but the diffuse lobe does not go through `h` at all, and
killing the whole sample there throws away most of what a matte surface would have gathered.
On a Cornell box it cost **7% of the overall brightness**, and up to 12% on the floor — with
every region losing energy in the same direction. That is the diagnostic: a genuine lighting
error is almost never a deficit of similar size on a red wall, a white floor and a metal
sphere at once.

Every probability in that tree cancels against the term it selects:

| Lobe | Weight after cancellation |
| --- | --- |
| reflection | `(F / pReflect) · G·\|v·h\| / (\|n·v\|·\|n·h\|)` |
| transmission | `((1 − F) / (1 − pReflect)) · G·\|v·h\| / (\|n·v\|·\|n·h\|)` |
| diffuse | `albedo · (1 − F') / ((1 − pReflect) · 1)` |

The `transmission` and `1 − transmission` factors cancel exactly against their own sub-choice
probabilities, so they never appear in a weight. `pReflect` is chosen as `luminance(F)`
because that *is* the energy split: for an untinted `F` the ratio `F / pReflect` is 1 and the
reflection weight reduces to `G·|v·h| / (|n·v|·|n·h|)` alone.

### `F'` — the one place the half-vector must be recomputed

The diffuse row above writes `F'`, not `F`, and the difference is not cosmetic.

The diffuse lobe's value is `albedo·(1 − F(v·h(l)))/π`, where `h(l)` is the half-vector of
the direction **actually sampled**. The `h` drawn from `D` belongs to the two specular lobes;
using its Fresnel term for the diffuse weight would evaluate the BSDF at a direction other
than the one being sampled, and the estimator would no longer be unbiased.

So the diffuse branch recomputes `h = normalize(v + l)` and its own `F'` after choosing `l`.
Using `h` freely to decide **which** lobe to sample is fine — a probability may depend on
anything already known. Using it to *evaluate* the lobe is not.

### Consistency with light sampling

`evalBrdf`, which next-event estimation calls with a direction chosen by the light rather
than by the surface, must describe the **same** BSDF. Its diffuse term therefore carries the
`(1 − transmission)` factor as well. If the sampler and the evaluator disagree, the image is
biased in a way that no amount of convergence removes — and the two are 200 lines apart in
the shader, which is exactly how that bug survives.

The transmission lobe is deliberately **not** added to `evalBrdf`. For a smooth dielectric it
is a delta function and contributes nothing to a light sample; for a rough one it would
contribute, but only for a light on the far side of the surface, which requires transmissive
shadow rays to have found it anyway. See [Limits](#limits-of-this-implementation).

## Beer–Lambert absorption

Inside a medium, radiance decays exponentially with the distance travelled:

```
T(d) = exp(−σ · d)
```

`σ` is the material's `absorption`, an extinction coefficient **per world unit**, one value
per colour channel. Absorbing more red than blue is what makes thick glass green.

Two consequences an author feels immediately:

- It is **not** a colour multiplier. `absorption: [0.5, 0.1, 0.1]` does not mean "half the
  red gets through"; it means red decays with a characteristic length of 2 world units.
- **Thickness matters.** Two slabs of the same glass, one twice as thick, differ by a
  *square*, not a factor of two. That is the cheapest way to check the implementation, and
  it is why the test scene has two slabs rather than one.

### Where it is applied

The attenuation belongs to a **segment**, not to a surface. The natural place is therefore at
the top of the next loop iteration, once the segment's length is known:

```
hit = trace(ray)
if inside a medium:
    throughput *= exp(−absorption · hit.t)
```

Applying it at the point of entry instead requires knowing where the ray will leave, which is
the one thing that has not been computed yet.

## Shadow rays that return a colour

`occluded()` answered a boolean and returned at the first thing in the way. Through glass
the honest answer is a transmittance:

```
transmittance = 1
repeat up to MAX_SHADOW_STEPS:
    hit = nearest hit before maxT
    if none:                  return transmittance
    if material is opaque:    return 0
    transmittance *= transmission
    if entering:  remember this material's absorption
    else:         forget it
    advance the origin past the hit
return transmittance          // budget exhausted: under-occlude rather than blacken
```

Three deliberate choices:

- **Shadow rays do not bend.** A refracted shadow ray would no longer point at the light, so
  the very notion of "is the light visible along this ray" stops being answerable this way.
  Every offline renderer makes this approximation, and it is *exactly* why the shadow under a
  glass sphere is an evenly dimmed disc with no bright spot — see
  [Caustics](#caustics) below.
- **Exhausting the step budget returns what was accumulated**, not zero. Under-occluding
  shows up as a slightly-too-bright patch; over-occluding shows up as a black band with no
  visible cause, which costs far more to diagnose.
- **A scene with no transmissive material keeps the old fast path.** The loop above traces
  the tape up to four times per light per bounce, against once with an early-out. The
  compiler knows whether any material transmits, so the shader is told, and every scene
  written before this iteration runs at exactly its previous cost.

## Caustics

A caustic is the bright spot a lens throws — light that was focused by a curved refracting
surface before it landed on a diffuse one. In path-tracing terms it is an **S-D-S** path:
specular (the glass), diffuse (the floor), specular (the eye's chain back).

### Why a backward path tracer never finds one

Trace the deliverable's geometry backwards from the camera:

1. A ray leaves the eye and lands on the floor.
2. Next-event estimation fires a shadow ray at the light. It passes through the glass, gets
   dimmed by absorption and Fresnel, and arrives — **unbent**. No focusing, no caustic.
3. The BSDF-sampled bounce leaves the floor in a cosine-weighted random direction. To
   contribute a caustic it must enter the glass, refract twice, exit, and land on the light.

Step 3 is the only path that carries the caustic, and its probability against an **idealised
point light** is exactly **zero** — a delta light has no area to hit. Against a
`pointLight { radius }` it is not zero, but the light is not geometry either: nothing in the
tape represents it, so a ray can never hit one.

### What makes it possible here

An **emissive solid** is geometry. It is in the tape, it can be hit, and iteration 4 already
counts its emission when a path lands on it. So the chain

```
floor --cosine--> glass --refract--> glass --refract--> emissive panel
```

is found, at a low but non-zero rate, with no new machinery at all: no extra pass, no photon
buffer, no OpenGL version bump. The caustic converges the way everything else does, only
slower, because only a small fraction of the floor's samples take that route.

This is why the glass test scene is lit by a **large emissive panel** rather than by a point
light. It is not decoration: it is the one configuration in which the deliverable is
reachable.

### The two options not taken

| Option | Why not |
| --- | --- |
| **Photon splatting** — emit photons from the light through transform feedback, project the deposits into screen space and add them | Sharper and much faster to converge. Costs a second trace implementation in a vertex shader, a feedback buffer, and a screen-space visibility test. Worth doing if caustics become a focus; not worth it to render one sphere. Note that it needs **no** version bump: transform feedback has been core since OpenGL 3.0, contrary to what the roadmap claimed |
| **Skip caustics** | Defensible, and it was on the table. Rejected because the emissive-solid route costs nothing to try and either works or produces a measurement worth publishing |

### Expect noise, and measure it

A caustic built this way is the noisiest thing in the image, because it is carried by the
rarest paths. The honest way to report it is a **ratio measured on the floor** — the lit spot
against a reference patch beside it — at several render times, rather than a screenshot
chosen for being flattering. If the ratio stays inside the noise, that is the result.

## Numerical notes

**Offset the next ray on the correct side.** After a reflection the new origin is
`p + n·bias`; after a transmission it is `p − n·bias`, because the ray is now on the other
side of the surface. Getting the sign wrong makes the ray immediately re-hit the face it just
crossed, and the path dies there. The symptom is glass that renders perfectly **black** —
which reads as an absorption bug and is not one.

**The normal always faces the ray.** `resolveRoot` and `hitNormal` between them guarantee it,
including for a ray that started inside a solid. So `eta = entering ? 1/ior : ior` and
`refract(−v, h, eta)` are correct in both directions with no special case.

**Reject microfacets facing away.** `sampleGgxHalf` can return an `h` with `v·h ≤ 0` at
grazing angles. Neither lobe is defined there; the sample must end the path rather than be
clamped.

**Total internal reflection forces the lobe choice.** With `F = 1` the transmission branch
would be selected 5% of the time and contribute exactly zero. Setting `pReflect = 1` under
total internal reflection costs one compare and reclaims those samples.

**Bounce budget.** Seeing *through* a glass sphere costs two bounces before the ray has
gone anywhere. `render.maxBounces: 4` — the default — shows a glass sphere with a black
interior, which looks like a transmission bug. A glass scene wants 8.

## Limits of this implementation

Everything below is a known limitation, not an open bug. Each one is listed with the wrong
image it produces, so that the wrong image can be recognised rather than investigated.

### Nested and overlapping media

The path carries **one** medium at a time — an absorption coefficient and a flag — not a
stack. A solid inside another solid, both transmissive, gives a wrong result: leaving the
inner one clears the medium entirely, so the remaining thickness of the outer one absorbs
nothing.

*Symptom:* the inner object appears surrounded by a halo of unnaturally clear glass.

Overlapping solids under `union` are **not** affected, and the reason is worth stating: the
interval algorithm coalesces overlapping spans, so two intersecting glass spheres form a
single span with a single pair of boundaries. There is no inner boundary to be confused by —
see [csg-raytracing.md](csg-raytracing.md#union--a--b).

### The camera must be in vacuum

The `η²` radiance factors cancel over a complete traversal, which assumes every path both
enters and leaves. A camera placed **inside** a transmissive solid breaks that assumption and
the image is scaled by a constant factor.

*Symptom:* uniformly too bright or too dark, with correct geometry. Nothing checks for it.

### Shadow rays do not refract

Stated above under [shadow rays](#shadow-rays-that-return-a-colour). Direct light through
glass is dimmed but never focused or displaced.

*Symptom:* the shadow of a glass sphere is an evenly dimmed disc. The caustic, when it
appears, arrives entirely through the indirect path.

### Transmission is invisible to next-event estimation

`evalBrdf` implements the reflection and diffuse lobes only. A rough transmissive surface
therefore receives no direct light *through* itself, only through the bounce loop.

*Symptom:* frosted glass lit from behind converges noticeably more slowly than the same
surface lit from the front.

### Absorption is the only volumetric effect

There is no scattering inside a medium: light travelling through glass is attenuated, never
redirected. Milk, wax, marble and skin — anything whose look comes from subsurface
scattering — are out of reach.

*Symptom:* a "translucent" material set up with high absorption looks like dark glass, not
like wax.

### One index of refraction per material, no dispersion

`ior` is a single number, not a function of wavelength, and the renderer carries three colour
channels rather than a spectrum. A prism produces no rainbow.

*Symptom:* white light stays white through a prism. Dispersion would need per-wavelength
paths, which is a spectral renderer, not a tweak.

### Fresnel uses Schlick, not the exact equations

Accurate to about 1% for dielectrics away from grazing angles, with the transmitted-angle
correction of the [trap section](#the-trap-schlick-seen-from-the-dense-side) making the
critical angle exact. The exact Fresnel equations are six lines and were not adopted only
because Schlick keeps every pre-iteration-5 scene bit-identical.

*Symptom:* none visible. Recorded because "why is this an approximation" is a fair question
to ask of the code.

### Emissive solids are still not sampled

Inherited from iteration 4 and unchanged: a CSG solid has no parameterisation, so
next-event estimation cannot target one. A **small** emissive source therefore stays noisy
however long it renders — and a caustic driven by one is the worst case of that, since it
also needs the rare refracted path.

*Symptom:* a small bright light gives a grainy image that cleans up far more slowly than the
same scene lit by `pointLight { radius }`.

### Fixed bounce depth

Also inherited: paths are cut at `maxBounces` with no Russian roulette, so the estimator is
**biased** — it systematically loses the energy carried by longer paths. Glass makes this
much more visible than opaque scenes did, since two bounces are spent crossing a single
sphere.

*Symptom:* the interior of a stack of glass objects darkens with depth.

## Sources

Provenance only; everything needed is above.

- Walter, Marschner, Li, Torrance — *Microfacet Models for Refraction through Rough
  Surfaces*, EGSR 2007. The BTDF, the refraction half-vector and the Jacobian.
- Schlick — *An Inexpensive BRDF Model for Physically-based Rendering*, 1994.
- Pharr, Jakob, Humphreys — *Physically Based Rendering*, 3rd ed., chapters 8 (specular
  reflection and transmission) and 11 (volume scattering).
- Jensen — *Realistic Image Synthesis Using Photon Mapping*, 2001. For the option not taken.
