# Lighting: path tracing, materials and convergence

This document is meant to be **self-sufficient**: everything needed to implement the
lighting side is here, so no further web research should be required. Sources are listed at
the end for provenance only. It is the companion to
[csg-raytracing.md](csg-raytracing.md), which covers ray/solid intersection and stops where
this one begins — at the moment a surface has been found.

## What changes, and why it has to

Up to iteration 3 a pixel was one ray, one surface, one shading call. Light travelled from a
light to a surface to the eye, and stopped. A constant `AMBIENT` term stood in for
everything that formulation cannot express: light arriving from other surfaces.

That constant is a lie with a specific cost. A red floor under a white ceiling should tint
the ceiling red, and no amount of tuning an ambient constant produces that, because the
constant does not know the floor is red or that it is below. **Colour bleeding is the
cheapest possible proof that light actually propagates**, and it is the deliverable of this
iteration.

## The rendering equation

The radiance leaving a point `x` towards `ω_o` is what it emits plus what it reflects:

```
L_o(x, ω_o) = L_e(x, ω_o) + ∫  f_r(x, ω_i, ω_o) · L_i(x, ω_i) · (n · ω_i) dω_i
                             Ω
```

- `L_e` — emitted radiance; zero for everything except an emissive material.
- `f_r` — the BRDF: what fraction of light arriving from `ω_i` leaves towards `ω_o`.
- `(n · ω_i)` — the cosine term. Light arriving edge-on spreads over more surface.
- `Ω` — the hemisphere above the surface.

It is recursive: `L_i` is another point's `L_o`. That recursion is the whole problem, and it
is why an integral over a hemisphere has to be estimated rather than solved.

### Monte Carlo, and why noise is the price

Pick directions at random rather than integrating analytically:

```
L_o ≈ L_e + (1/N) · Σ  f_r(ω_k) · L_i(ω_k) · (n · ω_k) / pdf(ω_k)
                    k
```

Each sample is a whole light path traced to some depth. The estimate is **unbiased** — it is
correct on average — and its error falls as `1/√N`. That square root is the reason the image
starts noisy and cleans up rather than appearing finished: four times the samples for half
the noise.

Division by `pdf` is what keeps it unbiased when the directions are not uniform. Choosing a
`pdf` shaped like the integrand is *importance sampling*, and it is the difference between an
image that converges in seconds and one that never does.

### The loop form

Recursion is unavailable — GLSL has none — and unnecessary. Carry a **throughput**: the
product of every weight so far.

```
radiance   = 0
throughput = 1

repeat maxBounces times:
    hit = trace(ray)
    if no hit:
        radiance += throughput * background
        break

    radiance += throughput * emission(hit)
    radiance += throughput * directLight(hit)        # see "Next-event estimation"

    (direction, weight) = sampleBrdf(hit)
    throughput *= weight
    ray = (hit.point + bias, direction)
```

`weight` is already `f_r · cos / pdf`. Everything below is about computing it.

## Materials: the metallic-roughness workflow

Four parameters, chosen because they are the ones an author can reason about:

| Field | Meaning |
| --- | --- |
| `color` | base colour — diffuse albedo for a dielectric, reflectance tint for a metal |
| `roughness` | `0` mirror-smooth, `1` fully matte |
| `metallic` | `0` dielectric (plastic, stone, paint), `1` metal |
| `emission` | radiance emitted, unbounded — `[0,0,0]` for everything that is not a light |

Two derived quantities, and the reason the workflow works:

```
F0           = mix(vec3(0.04), color, metallic)
diffuseAlbedo = color * (1.0 - metallic)
```

`F0` is the reflectance at normal incidence. Dielectrics reflect about **4%** of light
head-on regardless of colour — that is what `0.04` is, and it corresponds to an index of
refraction near 1.5, which is glass, plastic and most paints. Metals reflect much more, and
*tinted*: that is why a metal's `color` moves into `F0` rather than staying diffuse.

**A metal has no diffuse lobe at all.** Free electrons absorb whatever is not reflected, so
nothing scatters back out. That is exactly what `1 - metallic` encodes. Setting `metallic`
between 0 and 1 is physically meaningless for a single surface; it exists for blends and for
authoring convenience.

`metallic: 1, roughness: 0` is a mirror. This replaces the old `reflectivity` field, which
mixed a reflection into a Blinn-Phong surface with no energy accounting.

## The BRDF: Lambert + Cook-Torrance GGX

```
f_r = diffuseAlbedo·(1 - F)/π  +  D·G·F / (4·(n·v)·(n·l))
      ^^^^^^^^^^^^^^^^^^^^^^^^     ^^^^^^^^^^^^^^^^^^^^^^
      diffuse                      specular
```

The `1/π` on the diffuse term is not decoration: it is what makes a Lambertian surface
reflect exactly the light it receives and no more. Blinn-Phong has no such factor, which is
precisely why it cannot be summed over bounces without inventing or losing energy.

The `(1 - F)` is the other half of that accounting: whatever the specular lobe reflects is
not available to scatter diffusely. Dropping it is a common shortcut and shows up as
surfaces that appear to glow at grazing angles, where `F` approaches 1.

### D — Trowbridge-Reitz (GGX) distribution

The fraction of microfacets whose normal is `h`.

```
α = roughness²
D(h) = α² / (π · ((n·h)²·(α² - 1) + 1)²)
```

Squaring roughness is a perceptual choice, not a physical one: it makes the visible change
roughly linear as an author drags the value from 0 to 1.

### G — Smith geometry term

Microfacets shadow and mask one another.

```
G1(x) = 2·(n·x) / ( (n·x) + sqrt(α² + (1 - α²)·(n·x)²) )
G     = G1(v) · G1(l)
```

### F — Schlick's Fresnel approximation

Reflectance rises towards 1 at grazing angles, for every material.

```
F(v, h) = F0 + (1 - F0)·(1 - (v·h))⁵
```

Schlick's approximation is within a fraction of a percent of the exact Fresnel equations for
dielectrics, and it is the term iteration 5 will reuse for refraction.

## Importance sampling

Two lobes, sampled separately, each with a weight that collapses to something cheap.

### Diffuse — cosine-weighted hemisphere

Sample proportionally to the cosine term, so that grazing directions — which contribute
little — are rarely drawn.

```
r   = sqrt(u1)
φ   = 2π · u2
tangent-space direction = (r·cos φ, r·sin φ, sqrt(max(0, 1 - u1)))
pdf = cos θ / π
```

The weight is `(albedo·(1-F)/π) · cosθ / (cosθ/π)` — **the π and the cosine cancel
exactly**, leaving only what the surface actually scatters:

```
weight = diffuseAlbedo · (1 - F)
```

That cancellation is the reason cosine sampling is used and not uniform sampling. Getting it
wrong shows up as a surface that is too dark towards its silhouette.

### Specular — sample the GGX half-vector

Draw a microfacet normal `h` from `D`, then reflect the view direction about it.

```
φ      = 2π · u1
cos θ  = sqrt( (1 - u2) / (1 + (α² - 1)·u2) )
h      = spherical(θ, φ), taken into world space around n
l      = reflect(-v, h)

if (n · l) <= 0   ->   the sample went below the surface; terminate the path
```

`pdf(h) = D(h)·(n·h)`, and converting from half-vector to direction divides by `4·(v·h)`:

```
pdf(l) = D(h)·(n·h) / (4·(v·h))
```

Substituting into `f_r · cosθ / pdf`, **D cancels entirely**:

```
weight = F · G · (v·h) / ( (n·v) · (n·h) )
```

No `D` anywhere in the final expression. If an implementation still evaluates `D` in the
sampling weight, it has an error.

### Choosing between the lobes

Pick one lobe per bounce, with a probability proportional to how much each is likely to
matter, then divide the weight by that probability so the estimate stays unbiased.

```
pSpecular = luminance(F0) / (luminance(F0) + luminance(diffuseAlbedo))
```

Clamp `pSpecular` away from 0 and 1 (say to `[0.1, 0.9]`) so neither lobe is ever
unreachable, and terminate the path if both luminances are zero — a perfectly black surface
reflects nothing and there is nothing left to trace.

### Why `roughness: 0` needs a floor

At `α = 0` the distribution `D` becomes a Dirac delta: infinitely tall, zero width. The
sampling formula still produces the mirror direction, but `D` appears in intermediate values
and overflows.

```
α = max(roughness², MIN_ALPHA)      // MIN_ALPHA = 1e-4
```

A clamp is preferred over branching to a perfect-mirror case: one code path, no discontinuity
between `roughness: 0` and `roughness: 0.01`, and the visual difference from a true delta is
below a pixel. The cost is that a very low `α` concentrates energy into few samples, which is
one of the sources of fireflies below.

## Next-event estimation

Waiting for a randomly bounced ray to land on a light converges appallingly badly, and for a
small light it effectively never happens. So at every bounce, **sample the lights directly**
and trace a shadow ray. That reuses the `anyHit` machinery built in iteration 3 unchanged.

The contribution added at a hit point, for one light:

```
contribution = throughput · f_r(ω_light) · (n · ω_light) · L_arriving
```

with a shadow ray from the surface to the light deciding whether it is added at all.

### Point light, `radius = 0` — a delta

An idealised point has no area, so there is no direction to sample: there is exactly one.

```
ω     = normalize(lightPos - p)
d     = length(lightPos - p)
L     = color · intensity / d²
maxT  = d
```

**The inverse-square falloff is new in this iteration.** Iterations 2 and 3 omitted it, which
made brightness independent of distance — tolerable for a direct-lighting toy, incoherent the
moment energy has to balance across bounces. The visible consequence is that scenes written
before this change need much larger `intensity` values: a light 5 units away now delivers
1/25 of what it did.

### Directional light — also a delta

Infinitely far away, so no falloff and no distance limit.

```
ω     = -direction        // direction is where the light travels *to*
L     = color · intensity
maxT  = infinity
```

### Point light, `radius > 0` — a sphere, sampled through its visible cone

A sphere of radius `r` seen from distance `d` subtends a cone. Sampling uniformly inside that
cone is exact, cheap, and avoids the wasted samples that sampling the whole sphere's surface
would produce (half of it faces away).

```
cos θmax = sqrt(1 - (r/d)²)
cos θ    = 1 - u1·(1 - cos θmax)
φ        = 2π·u2
ω        = spherical(θ, φ), around the axis towards the light's centre
pdf      = 1 / (2π·(1 - cos θmax))
```

The sphere's radiance is set so that **changing `radius` softens the shadow without changing
the brightness**:

```
L = color · intensity / (π · r²)
```

That is the normalisation that makes the estimator agree with the delta case in the limit.
As `r → 0`, `1 - cos θmax → r²/(2d²)`, so the solid angle `2π(1 - cos θmax) → π·r²/d²`, and

```
L / pdf  =  [intensity/(π r²)] · [π r²/d²]  =  intensity / d²
```

which is exactly the point-light expression. `radius` is therefore a pure softness control,
which is the property that makes it usable.

Two cases to handle rather than let the arithmetic produce `NaN`:

- `d <= r` — the shading point is inside the light. Skip the light; there is no cone.
- The shadow ray's `maxT` is the near intersection with the light sphere, not `d`:
  `tLight = dot(toCentre, ω) - sqrt(max(0, r² - (|toCentre|² - dot(toCentre, ω)²)))`.
  Using `d` instead lets the far half of the light shadow its own near half.

### Emissive surfaces are *not* sampled

An emissive material is found only when a bounced ray happens to hit it.

The reason is structural: **a CSG solid cannot be sampled uniformly.** There is no
parameterisation of `difference { box, sphere }` — no way to pick a point on its surface with
a known density, which is exactly what direct sampling requires. Nothing about the interval
algorithm provides one, and inventing one for the general case is a research problem, not an
iteration.

The consequence is honest and must be stated in the user-facing docs: **a large emissive
surface converges well; a small one is noisy.** `pointLight { radius }` is the way to *light*
a scene; `emission` is the way to be *seen* — a visible bulb, a glowing panel — and iteration
5 needs it for caustics.

### Why there is no multiple importance sampling

MIS exists to combine two strategies that can both find the same path, without
double-counting it. Here, neither overlap exists:

- `pointLight` and `directionalLight` are **not geometry**. No ray can hit them, so a bounced
  ray never finds a light that NEE already accounted for.
- Emissive solids **are** geometry but are **never** sampled by NEE, so a bounced ray hitting
  one is the only way that path is ever counted.

Every path is therefore counted exactly once by exactly one strategy. This is a real
simplification, not an oversight — a reviewer expecting to find MIS should read this
paragraph. It stops being true the moment emissive solids become directly sampleable, and
that is when MIS has to arrive with them.

## Progressive accumulation

One sample per pixel per frame is nowhere near enough. Rather than loop inside the shader,
average across frames: the window stays responsive and the image improves while it is
watched.

### The running average

```
out = mix(history, sample, 1.0 / (frameIndex + 1))
```

Not a sum divided by a count. A sum grows without bound and loses precision in a 32-bit float
long before a long render finishes; the running average stays in the range of the values
themselves. It is algebraically identical for the first few thousand frames and better
afterwards.

### Ping-pong, not additive blending

Two `RGBA32F` textures with a framebuffer each, swapped every frame: read one, write the
other. Blending additively into a single float target would work on most drivers and is less
code, but 32-bit float blending is exactly the kind of support that varies. Reading a texture
and writing another depends on nothing.

Everything used here — float textures, framebuffer objects — is core in OpenGL 3.3. **This
does not force a version bump.**

### When to reset

Anything that changes what a sample means invalidates every sample already taken. Reset the
frame counter and clear the buffer on:

- a framebuffer resize (the pixels no longer correspond);
- any change to the camera or the scene.

Failing to reset shows as an image that keeps a ghost of the previous state, fading slowly
rather than disappearing.

### Free antialiasing

Since samples accumulate anyway, jitter the primary ray inside its pixel:

```
ndc = vNdc + (u - 0.5) * 2.0 * invResolution
```

One line, and edges stop being staircases. There is no reason not to.

## Random numbers in GLSL 3.30

GLSL 3.30 has `uint` and bitwise operators, so a proper integer hash is available. A PCG
step is small, fast and has none of the visible structure that `fract(sin(dot(...)))`
produces:

```glsl
uint pcg(inout uint state)
{
    state = state * 747796405u + 2891336453u;
    uint word = ((state >> ((state >> 28u) + 4u)) ^ state) * 277803737u;
    return (word >> 22u) ^ word;
}

float rand(inout uint state) { return float(pcg(state)) * (1.0 / 4294967296.0); }
```

Seed it from the pixel **and** the frame index, hashed together — not concatenated, or
neighbouring pixels start with neighbouring states and the first frames show visible
structure.

**The state has to be threaded by hand through every function that draws a number.** GLSL has
no global mutable state, so `inout uint` travels down the whole call chain. Copying the seed
instead of advancing it gives correlated noise, which does not look like noise: it looks like
banding or a repeating pattern, and it is easy to misread as a geometry bug.

## Tone mapping and gamma

Accumulated radiance is unbounded — a light is not limited to 1.0 — while a display expects
`[0,1]` in a non-linear space. Two steps, in this order, in a separate resolve pass:

```glsl
color = accumulated * exposure;
color = acesFilmic(color);          // maps [0, inf) into [0, 1]
color = pow(color, vec3(1.0/2.2));  // linear -> sRGB-ish
```

The ACES approximation is five constants and is markedly better than clamping, which posts
every bright region as a flat white blob:

```glsl
vec3 acesFilmic(vec3 x)
{
    const float a = 2.51, b = 0.03, c = 2.43, d = 0.59, e = 0.14;
    return clamp((x * (a * x + b)) / (x * (c * x + d) + e), 0.0, 1.0);
}
```

**Skipping either step makes a correct render look like a lighting bug.** Without tone
mapping, anything above 1 clips and the image looks blown out; without gamma, everything
looks far too dark, and the instinctive response — raising the light intensity — produces a
picture that is wrong in a different way.

## Fireflies

Isolated pixels far brighter than their neighbours, which the average pulls down only over
many frames. They come from a sample with a large contribution and a small pdf: a nearly
specular bounce that happens to land on a light, or a very low `α` concentrating a lobe.

The pragmatic answer is to clamp the contribution of a single indirect sample:

```glsl
radiance += min(contribution, FIREFLY_CLAMP);
```

This is **biased** — it discards energy that genuinely belongs in the image — and it is
recorded here as a knob rather than applied silently, because a clamp low enough to remove
fireflies is also low enough to dim a legitimately bright reflection. Start with it generous
and lower it only if the noise is intolerable.

## Numerical notes

- Offset the origin of every bounced and shadow ray along the **normal**, not along the
  ray. Inside a `difference` cavity the normal points into the hollow, which is the side the
  ray must start on. Same rule and same reason as the shadow bias in
  [csg-raytracing.md](csg-raytracing.md#shadows).
- Terminate a path whose throughput has become negligible; there is nothing left to gather
  and the remaining bounces are pure cost.
- Guard every division by a `pdf`. A `pdf` of zero means the sample was impossible, and the
  correct response is to drop the sample, not to produce an infinity that poisons the running
  average for every subsequent frame — one `NaN` in the history is permanent.

## Sources

- J. T. Kajiya, *The Rendering Equation*, SIGGRAPH 1986 — the formulation this document uses.
- B. Walter et al., *Microfacet Models for Refraction through Rough Surfaces*, EGSR 2007 —
  GGX/Trowbridge-Reitz, the Smith geometry term, and the half-vector sampling derivation.
- C. Schlick, *An Inexpensive BRDF Model for Physically-based Rendering*, Eurographics 1994 —
  the Fresnel approximation.
- B. Burley, *Physically-Based Shading at Disney*, SIGGRAPH 2012 course — the
  metallic-roughness parameterisation and the `roughness²` remapping.
- M. Pharr, W. Jakob, G. Humphreys, *Physically Based Rendering*, 3rd ed. — Monte Carlo
  estimators, cone sampling of spherical lights, and next-event estimation.
- K. Narkowicz, *ACES Filmic Tone Mapping Curve*, 2015 — the five-constant approximation.
- M. E. O'Neill, *PCG: A Family of Simple Fast Space-Efficient Statistically Good Algorithms
  for Random Number Generation*, 2014.
