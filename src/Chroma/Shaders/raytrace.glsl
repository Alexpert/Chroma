// The ray tracer. One primary ray per pixel, intersected against the scene.
//
// This file is HALF of the fragment shader. The geometry -- the CSG tree, the span-list types,
// the boolean operators and the per-leaf constants -- is generated for each scene by
// Chroma.Core.Codegen and spliced in at the @chroma:geometry marker below. Everything here is
// hand-written and scene-independent: the primitive maths, the polynomial solvers, the normals,
// the path tracer. Run with --emit-shader to read a whole assembled shader.
//
// See documents/code-generation.md for why the scene is compiled to source rather than to a
// buffer an interpreter walks, and documents/csg-raytracing.md for the algorithm itself.
//
// A primitive does not answer "where is your nearest surface"; it returns every stretch of
// the ray that lies inside it, and the boolean operators merge those stretches. That is what
// makes a spherical cavity in a box come out with its normals pointing the right way.
#version 330 core

// --- Scene specialisation ----------------------------------------------------------------
//
// Three switches, supplied by the host as #define symbols read off the compiled scene. They
// were uniforms until iteration 11, and moving them was worth up to a factor of two.
//
// A uniform is constant for the whole draw, so a branch on one costs nothing to *execute* --
// every thread in a warp takes the same side. What it costs is to EXIST: the untaken side is
// still compiled, still holds registers in the schedule around it, and this shader sits close
// enough to the occupancy cliff that dead code is expensive. Adding one bounding-box branch
// that fog.chroma never executed cost that scene 2.3x. The same branch behind a #if costs it
// nothing at all.
//
// The scene is fixed for the life of the program, so there is nothing these can go out of
// step with. Defaults are here so the file still compiles when opened by a tool that knows
// nothing about the host.
#ifndef CHROMA_TRANSMISSION
#define CHROMA_TRANSMISSION 1   // some material lets light through
#endif

#ifndef CHROMA_MEDIA
#define CHROMA_MEDIA 1          // some material scatters inside its volume
#endif

// Which shader stage this file is being compiled as.
//
// One body, two stages. The tracer is a fragment shader on OpenGL 3.3 and a compute shader from
// 4.3 up, and the difference between them is three accessor macros and the eight lines of main
// at the bottom of this file -- deliberately, because two copies of a path tracer is two path
// tracers to keep correct. Everything between the two is stage-independent and is not compiled
// twice; it is written once and reads the same either way.
//
// The reason for the compute stage is not compute: it is that NVIDIA's fragment pipeline compiles
// through its assembly profile gp5fp, whose programs are capped at about 65,000 instructions, and
// a generated scene reaches that. See documents/gpu-backends.md.
#ifndef CHROMA_COMPUTE
#define CHROMA_COMPUTE 0
#endif

// Where the three scene tables live: shader storage buffers, or texture buffers read through a
// sampler. Independent of the stage on purpose -- a compute shader can read either, and which one
// is faster is a measurement rather than a deduction. A texture buffer goes through the read-only
// texture cache, which suits the shading path's short scattered walks over a contour; a storage
// buffer is the newer and more direct thing. Only the fallback tier is forced, because OpenGL 3.3
// has no storage buffers at all.
#ifndef CHROMA_STORAGE_BUFFERS
#define CHROMA_STORAGE_BUFFERS CHROMA_COMPUTE
#endif

// --- Limits and shared constants -------------------------------------------------------
// PRIMITIVE_TEXELS and MATERIAL_TEXELS mirror GpuLayout on the C# side. Nothing checks that
// the two agree, so they change together or not at all.
//
// There are no MAX_SPANS, MAX_STACK, MAX_CROSSINGS, MAX_SWEEP_EVENTS or MAX_BLOB_EVENTS any
// more. Every one of them was an array sized for the worst scene anyone might write, paid for
// by every scene that did not; each is now sized by the emitter from the node that owns it.
// That is the whole point of generating the geometry, and it is what makes a lathe whose
// outline needs 48 crossings possible at all -- the shared array held 32 and truncated in
// silence.
const int PRIMITIVE_TEXELS = 5;   // (kind, materialIndex, paramA, paramB) + 4 matrix rows
const int MATERIAL_TEXELS  = 4;   // colour+roughness, emission+metallic, absorption+transmission, ior+medium

const int MAX_LIGHTS = 8;

const int KIND_SPHERE   = 0;
const int KIND_BOX      = 1;
const int KIND_CYLINDER = 2;
const int KIND_CONE     = 3;
const int KIND_PLANE    = 4;
const int KIND_TORUS    = 5;
const int KIND_PRISM    = 6;
const int KIND_LATHE    = 7;
const int KIND_BLOB     = 8;
const int KIND_SWEEP    = 9;

// EPS is the geometric tolerance, in world units. TINY only guards divisions: a local ray
// direction can be legitimately small under a large scale, so reusing EPS there would
// reject perfectly good geometry.
const float EPS  = 1e-4;
const float TINY = 1e-12;
const float INF  = 1e30;
const float PI   = 3.14159265359;

// Larger than EPS on purpose: the hit point carries rounding proportional to t, so a bias
// sized for t = 0 stipples the far side of a large scene.
const float SHADOW_BIAS = 1e-3;

// At roughness 0 the GGX distribution becomes a Dirac delta: infinitely tall, zero wide,
// and it overflows. Clamping rather than branching to a perfect-mirror case keeps one code
// path and leaves no discontinuity between roughness 0 and roughness 0.01.
const float MIN_ALPHA = 1e-4;

// Ceiling on a single *indirect* sample. A near-specular bounce landing on a light produces
// a value the running average takes thousands of frames to dilute -- a firefly. This is
// biased: it discards energy that genuinely belongs in the image. It is generous on purpose,
// and it never touches the directly visible lighting.
const float FIREFLY_CLAMP = 20.0;

// There is deliberately no Russian roulette here. It was implemented, measured on the metric
// iteration 11 is judged by, and removed: it reached 6% error on cornell.chroma in 116.05 s
// against 110.55 s without it. The reason is architectural rather than a tuning failure -- a
// warp costs whatever its longest-lived thread costs, so killing paths does not free the lane
// they ran in, while the survivors' inflated weights are variance paid for regardless.
// documents/performance.md carries the numbers and the two variants that were tried.

// Occluders one shadow ray will pass through before giving up. A ray that runs out returns
// the transmittance it has accumulated rather than zero: under-occluding shows up as a
// slightly bright patch, over-occluding as a black band with no visible cause.
const int MAX_SHADOW_STEPS = 4;

// What a ray that escapes the scene brings back. This is no longer a backdrop colour: a
// path tracer gathers it as radiance, so it is a uniform environment light. Black means the
// only light in the scene is the light the file declares, which is what a closed room needs
// and what makes an energy balance checkable. Raising it lights everything from every
// direction at once -- useful, but a decision the scene should make, not a constant here.
//
// There is deliberately no AMBIENT constant any more. It stood in for light arriving from
// other surfaces, which this shader now computes for real; keeping it would count that
// light twice and put the render back out of energy balance.
const vec3 BACKGROUND = vec3(0.0);

// --- Inputs ----------------------------------------------------------------------------

// The three scene tables are read when SHADING only. A normal is recomputed once per hit, from
// whichever surface turned out to be visible, and that one fetch per bounce is not worth turning
// into a branch per leaf in the generated source. The span path -- the hot one, run for every ray
// against every root -- carries its transforms and its outlines as constants and touches none of
// them.
//
// PRIMITIVE/MATERIAL/SHAPE are the only place the two stages differ about data. A texture buffer
// and a shader storage buffer hold the same bytes and are indexed the same way by the same
// integer; only the syntax of the fetch differs, so only the syntax is behind the switch. The
// storage-buffer path is the newer one and the better one -- no size cap worth naming, no texture
// unit to bind, and an explicit binding point rather than a uniform the driver may strip.
#if CHROMA_STORAGE_BUFFERS

layout(std430, binding = 0) readonly buffer PrimitiveBuffer { vec4 texels[]; } bPrimitives;
layout(std430, binding = 1) readonly buffer MaterialBuffer  { vec4 texels[]; } bMaterials;
layout(std430, binding = 2) readonly buffer ShapeBuffer     { vec4 texels[]; } bShapes;

#define PRIMITIVE(i) bPrimitives.texels[i]
#define MATERIAL(i)  bMaterials.texels[i]
#define SHAPE(i)     bShapes.texels[i]

#else

uniform samplerBuffer uPrimitives;
uniform samplerBuffer uMaterials;

// Contour points, blob components and sweep spheres: one texel each, at the offset the
// primitive record's parameter slot names.
uniform samplerBuffer uShapes;

#define PRIMITIVE(i) texelFetch(uPrimitives, i)
#define MATERIAL(i)  texelFetch(uMaterials, i)
#define SHAPE(i)     texelFetch(uShapes, i)

#endif

// Right and Up already carry the field of view and the aspect ratio, so building a ray is
// one add and one normalise.
uniform vec3 uCameraPosition;
uniform vec3 uCameraForward;
uniform vec3 uCameraRight;
uniform vec3 uCameraUp;

uniform int   uLightCount;
uniform int   uLightKind[MAX_LIGHTS];    // 0 point, 1 directional
uniform vec3  uLightVector[MAX_LIGHTS];  // position, or the direction the light travels
uniform vec3  uLightColor[MAX_LIGHTS];   // colour already scaled by intensity
uniform float uLightRadius[MAX_LIGHTS];  // 0 is an idealised point; above 0, a sphere

uniform int uMaxBounces;

// CHROMA_TRANSMISSION and CHROMA_MEDIA used to be the uniforms uHasTransmission and uHasMedia.
// They say the same thing -- does any material let light through, does any scatter inside its
// volume -- and they say it at compile time instead, for the reason given at the top of the
// file: an opaque scene should not merely skip the transmissive shadow walk, it should not be
// compiled with one.

// Everything accumulated before this frame, and how many samples that is. The shader writes
// a running average rather than a growing sum: a sum loses precision in a 32-bit float long
// before a long render finishes, while an average stays in the range of the values.
//
// The two stages hold the running average differently, and the compute stage's way is the better
// one. A fragment shader cannot read the target it is writing, so the accumulation is two textures
// ping-ponged, one read and one written; a compute shader can, so it is one image read and written
// in place. That halves the memory and removes the swap -- see AccumulationBuffer.
#if CHROMA_COMPUTE
layout(rgba32f, binding = 0) uniform image2D uAccumulation;
#else
in  vec2 vNdc;
out vec4 FragColor;
uniform sampler2D uHistory;
#endif

uniform int uSampleIndex;

// Half a pixel in NDC, for jittering the primary ray. Since frames accumulate anyway,
// antialiasing costs one line.
uniform vec2 uInvResolution;

// --- Spans -----------------------------------------------------------------------------

// The stretch of a ray that lies inside a solid, with the surface crossed at each end.
//
// The surface is a reference, not a normal: carrying position and normal at both ends of
// every span, times every span a node holds, times every node live at once, is far more
// register pressure than a fragment shader can afford. The normal is recomputed once, at the
// end, from the one surface that turned out to be visible.
//
//   surf =  0            no surface (the +/-infinity ends of a complement)
//   surf = +(prim + 1)   the primitive's outward normal
//   surf = -(prim + 1)   that normal, negated -- the surface came from a subtracted operand
//
// The two codes share ONE int, sixteen bits each, and that is the largest single speed-up in
// this renderer's history: 1.75x on cornell.chroma, 1.78x on glass.chroma, 1.97x on
// fog.chroma, 8.3x on lattice.chroma, with every image bit-identical.
//
// The reason is that a Span is the unit this shader has most of. Under the interpreter this
// was eight of them per list, four lists live at once whatever the scene said, plus one more
// for each merge: four words per span came to 132 words, far past what a fragment shader holds
// in registers, so the whole span stack spilled to local memory. Three words took that down by
// a quarter, and a quarter is what the shader was over by. Generating the lists took the count
// itself down -- a scene of two spheres now holds two spans, not thirty-two -- and this packing
// is still worth having on top of that.
//
// Sixteen bits is ample and is not a gamble: the code is +/- (leaf index + 1), and no scene
// reaches 32767 leaves before the generated source becomes the binding limit.
struct Span
{
    float tIn;
    float tOut;
    int   surf;
};

// A solid, for one ray, is a list of spans sorted by tIn, disjoint and non-touching -- an
// invariant the operators consume and must restore. The LIST type is generated: GLSL makes an
// array's length part of its type, so a node producing two spans and one producing twenty-four
// are different types, and generating them is what lets each node hold exactly its own worst
// case instead of the worst case of every scene.
//
// Biased into the unsigned range before packing. A negative code is meaningful -- it is what
// marks a surface that came from a subtracted operand, and getting that wrong turns the inside
// of every drilled hole black -- and a negative number shifted into a bit field loses it.
const int SURF_BIAS = 32768;

int packSurf(int entry, int exit_)
{
    return (entry + SURF_BIAS) | ((exit_ + SURF_BIAS) << 16);
}

// The mask on the high half is not decoration. GLSL's >> on a signed int is arithmetic, so a
// code whose bit 31 is set smears ones down through the shift; masking after it is what keeps
// the two halves independent.
int surfIn(Span s)  { return (s.surf & 0xFFFF) - SURF_BIAS; }
int surfOut(Span s) { return ((s.surf >> 16) & 0xFFFF) - SURF_BIAS; }

// tIn > tOut, so the width test below rejects it without a separate flag.
Span noSpan() { return Span(1.0, -1.0, packSurf(0, 0)); }

// Inside a span function the surface code is a boolean: 1 for "this primitive's surface",
// 0 for an end at infinity, which only an unbounded primitive can produce. tagSpan turns the
// 1s into the leaf's real index -- a span function does not know which leaf it is being
// evaluated for, and should not have to.
int surfCode(float t) { return abs(t) >= INF ? 0 : 1; }

Span spanOf(float tIn, float tOut)
{
    return Span(tIn, tOut, packSurf(surfCode(tIn), surfCode(tOut)));
}

// Names the leaf a span came from, turning the span function's booleans into a real index.
// A span function has no idea which leaf it is being evaluated for, and should not have to;
// the generated wrapper knows, because it is the leaf.
Span tagSpan(Span s, int leaf)
{
    return Span(
        s.tIn,
        s.tOut,
        packSurf(surfIn(s) != 0 ? leaf + 1 : 0, surfOut(s) != 0 ? leaf + 1 : 0));
}

// --- Polynomial solvers ------------------------------------------------------------------
// A torus is quartic and a blob's field is a sum of quartics, so one solver serves both.

float cbrt(float x) { return sign(x) * pow(abs(x), 1.0 / 3.0); }

// The largest real root of x^3 + a2 x^2 + a1 x + a0, by Cardano.
//
// Every real cubic has at least one real root, so this always has an answer -- which is what
// lets the quartic below use it without a failure path. Largest rather than any: the
// resolvent it is used for needs a POSITIVE root, and the largest real root of that
// particular cubic is guaranteed to be one.
float largestCubicRoot(float a2, float a1, float a0)
{
    float shift = a2 / 3.0;

    // Depressed cubic w^3 + p w + q, with x = w - a2/3.
    float p = a1 - a2 * shift;
    float q = a0 - a1 * shift + 2.0 * shift * shift * shift;

    float half_ = 0.5 * q;
    float disc  = half_ * half_ + p * p * p / 27.0;

    if (disc >= 0.0)
    {
        float s = sqrt(disc);
        return cbrt(-half_ + s) + cbrt(-half_ - s) - shift;
    }

    // Three real roots. Substituting w = 2r cos(theta) collapses the cubic to cos(3 theta),
    // and theta in [0, pi/3] picks the largest of the three.
    float r   = sqrt(-p / 3.0);
    float phi = acos(clamp(-half_ / (r * r * r), -1.0, 1.0));

    return 2.0 * r * cos(phi / 3.0) - shift;
}

// Two Newton steps against the polynomial as written.
//
// Ferrari's closed form loses precision through the resolvent cubic, and on a torus that
// shows up as a ring of speckles where the ray is nearly tangent to the surface -- exactly
// the places a closed form is least accurate and the eye is most likely to be looking.
//
// It is a REFINEMENT and is guarded as one. Where two roots nearly coincide the derivative
// nearly vanishes, and an unguarded step then jumps somewhere unrelated -- which downstream
// is not a slightly wrong surface but an entirely invented one. A blob's silhouette is made
// of near-double roots from end to end, and the symptom is a dark shell floating around the
// shape. So a step is taken only if it is small and only if it actually reduces the residual.
float polishQuartic(float t, float a3, float a2, float a1, float a0)
{
    float best     = t;
    float residual = abs((((t + a3) * t + a2) * t + a1) * t + a0);

    for (int i = 0; i < 2; ++i)
    {
        float f  = (((t + a3) * t + a2) * t + a1) * t + a0;
        float df = ((4.0 * t + 3.0 * a3) * t + 2.0 * a2) * t + a1;

        if (abs(df) < TINY) break;

        float step = f / df;
        if (abs(step) > 0.01 * (abs(t) + 1.0)) break;

        t -= step;

        float moved = abs((((t + a3) * t + a2) * t + a1) * t + a0);
        if (moved >= residual) break;

        best     = t;
        residual = moved;
    }

    return best;
}

// Where the quartic solver leaves its answer.
//
// A global rather than an out parameter, and that is not a stylistic choice. The driver inlines
// every call in this shader and then allocates storage per VARIABLE rather than per live range,
// so an array parameter becomes a fresh array at every one of a scene's call sites; a chess set
// reached "error C5041: cannot locate suitable resource to bind variable" on this array alone.
// Only one solve is ever in flight, so one array is all there is to want. Every scratch array in
// the geometry path is a global for the same reason -- see Codegen/GeometryEmitter.
float gRoots[4];

// Real roots of t^4 + a3 t^3 + a2 t^2 + a1 t + a0, ascending, left in gRoots. Returns how many
// there are: 0, 2 or 4, since complex roots of a real polynomial come in pairs.
//
// Ferrari: depress the quartic, solve one resolvent cubic for the number that splits it into
// two quadratics, then solve those.
int solveQuartic(float a3, float a2, float a1, float a0)
{
    gRoots[0] = 0.0;
    gRoots[1] = 0.0;
    gRoots[2] = 0.0;
    gRoots[3] = 0.0;

    float shift = 0.25 * a3;

    // Depressed quartic u^4 + p u^2 + q u + r, with t = u - a3/4.
    float p = a2 - 6.0 * shift * shift;
    float q = a1 - 2.0 * a2 * shift + 8.0 * shift * shift * shift;
    float r = a0 - a1 * shift + a2 * shift * shift - 3.0 * shift * shift * shift * shift;

    // The resolvent's constant term is -q^2, so its value at 0 is never positive and its
    // largest real root is never negative. alpha = sqrt(that root) is therefore always real.
    float alpha2 = max(largestCubicRoot(2.0 * p, p * p - 4.0 * r, -q * q), 0.0);
    float alpha  = sqrt(alpha2);

    float beta   = 0.0;
    float gamma  = 0.0;
    bool  usable = alpha > TINY;

    if (usable)
    {
        // (u^2 + alpha u + beta)(u^2 - alpha u + gamma), whose expansion reproduces p, q
        // and r exactly.
        beta  = 0.5 * (p + alpha2 - q / alpha);
        gamma = 0.5 * (p + alpha2 + q / alpha);

        // Ferrari guarantees beta * gamma == r, and checking it is what makes this solver
        // trustworthy rather than merely correct on paper.
        //
        // When q is zero the resolvent's root is zero too -- but it is computed as the
        // difference of two nearly equal cube roots, so it lands on a small POSITIVE number
        // as readily as on zero. sqrt() then turns 1e-5 of noise into an alpha of 3e-3:
        // large enough to pass any absolute test, small enough to make q / alpha pure
        // garbage. The identity below is the only thing that notices. Without it a blob
        // renders wrapped in an onion of invented shells, each one a set of "roots" whose
        // residual is not small at all.
        float scale = max(max(abs(beta * gamma), abs(r)), 1.0);
        usable = abs(beta * gamma - r) <= 1e-3 * scale;
    }

    if (!usable)
    {
        // No usable split, which means q is negligible: the quartic is a quadratic in u^2
        // and factors without the resolvent's help. Dropping a q that was small rather than
        // exactly zero costs a little accuracy, and the polish below recovers it.
        float d = p * p - 4.0 * r;
        if (d < 0.0) return 0;

        float s = sqrt(d);
        alpha  = 0.0;
        alpha2 = 0.0;
        beta   = 0.5 * (p + s);
        gamma  = 0.5 * (p - s);
    }

    int count = 0;

    float d1 = 0.25 * alpha2 - beta;
    if (d1 >= 0.0)
    {
        float s = sqrt(d1);
        gRoots[count] = -0.5 * alpha - s - shift; count++;
        gRoots[count] = -0.5 * alpha + s - shift; count++;
    }

    float d2 = 0.25 * alpha2 - gamma;
    if (d2 >= 0.0)
    {
        float s = sqrt(d2);
        gRoots[count] = 0.5 * alpha - s - shift; count++;
        gRoots[count] = 0.5 * alpha + s - shift; count++;
    }

    for (int i = 0; i < 4; ++i)
    {
        if (i >= count) break;
        gRoots[i] = polishQuartic(gRoots[i], a3, a2, a1, a0);
    }

    // Insertion sort over at most four values. The two quadratics are each sorted but they
    // interleave, and everything downstream pairs consecutive roots into spans.
    for (int i = 1; i < 4; ++i)
    {
        if (i >= count) break;

        float key = gRoots[i];
        int   j   = i - 1;

        for (int k = 0; k < 4; ++k)
        {
            if (j < 0 || gRoots[j] <= key) break;
            gRoots[j + 1] = gRoots[j];
            j--;
        }

        gRoots[j + 1] = key;
    }

    return count;
}

// --- Primitives ------------------------------------------------------------------------
// Every primitive is evaluated in its own canonical space. Six of the nine need no shape
// parameters at all -- their dimensions live entirely in the matrix. The cone's taper and the
// torus's minor radius are ratios, which no affine map can absorb, and the prism, lathe and
// blob are defined by a list rather than by a formula; those five numbers arrive in the
// primitive record's two parameter slots.

// The slab 0 <= y <= 1, shared by the cylinder, the cone and the prism. False means the ray
// misses it entirely, which is the one case the caller cannot read off tIn and tOut.
bool slabY(vec3 ro, vec3 rd, out float tIn, out float tOut)
{
    tIn  = -INF;
    tOut =  INF;

    if (abs(rd.y) < TINY)
    {
        // Parallel to the slab: inside it for the whole ray, or never.
        return ro.y >= 0.0 && ro.y <= 1.0;
    }

    float ta = (0.0 - ro.y) / rd.y;
    float tb = (1.0 - ro.y) / rd.y;

    tIn  = min(ta, tb);
    tOut = max(ta, tb);
    return true;
}

// Canonical unit sphere at the origin. rd is deliberately not assumed to be unit length.
Span sphereSpan(vec3 ro, vec3 rd)
{
    float a = dot(rd, rd);
    float b = dot(ro, rd);
    float c = dot(ro, ro) - 1.0;

    float disc = b * b - a * c;
    if (disc < 0.0)
    {
        return noSpan();
    }

    float s = sqrt(disc);
    return spanOf((-b - s) / a, (-b + s) / a);
}

// Canonical box, [-1, 1] on every axis. The slab test relies on 1.0 / 0.0 being infinity,
// which is well defined in GLSL; a ray parallel to a slab then fails tIn > tOut on its own.
Span boxSpan(vec3 ro, vec3 rd)
{
    vec3 inv = 1.0 / rd;
    vec3 t1  = (vec3(-1.0) - ro) * inv;
    vec3 t2  = (vec3( 1.0) - ro) * inv;

    vec3 lo = min(t1, t2);
    vec3 hi = max(t1, t2);

    float tIn  = max(lo.x, max(lo.y, lo.z));
    float tOut = min(hi.x, min(hi.y, hi.z));

    return tIn > tOut ? noSpan() : spanOf(tIn, tOut);
}

// Canonical cylinder: radius 1 about +Y, from y = 0 to y = 1. It is the intersection of an
// infinite tube with a slab, and both are spans, so no special case is needed for the caps.
Span cylinderSpan(vec3 ro, vec3 rd)
{
    float slabIn;
    float slabOut;
    if (!slabY(ro, rd, slabIn, slabOut)) return noSpan();

    float tubeIn  = -INF;
    float tubeOut =  INF;

    float a = rd.x * rd.x + rd.z * rd.z;
    if (a < TINY)
    {
        // Parallel to the axis: either always inside the tube or never.
        if (ro.x * ro.x + ro.z * ro.z > 1.0)
        {
            return noSpan();
        }
    }
    else
    {
        float b = ro.x * rd.x + ro.z * rd.z;
        float c = ro.x * ro.x + ro.z * ro.z - 1.0;

        float disc = b * b - a * c;
        if (disc < 0.0)
        {
            return noSpan();
        }

        float s = sqrt(disc);
        tubeIn  = (-b - s) / a;
        tubeOut = (-b + s) / a;
    }

    float tIn  = max(tubeIn, slabIn);
    float tOut = min(tubeOut, slabOut);

    return tIn > tOut ? noSpan() : spanOf(tIn, tOut);
}

// Canonical cone: radius 1 at y = 0 tapering to `cap` at y = 1, capped at both ends.
//
// The lateral surface is x^2 + z^2 = (1 + m y)^2 with m = cap - 1, which is a quadric like
// the cylinder's tube -- and reduces to it exactly when cap is 1. What the cylinder does not
// have is the case below where the quadratic opens downward: the ray then runs between the
// two nappes of the double cone and its inside is two half-lines rather than one interval.
// Only one of them can survive the slab, because cap >= 0 puts the mirror nappe at y > 1.
Span coneSpan(vec3 ro, vec3 rd, float cap)
{
    float slabIn;
    float slabOut;
    if (!slabY(ro, rd, slabIn, slabOut)) return noSpan();

    float m = cap - 1.0;
    float g = 1.0 + m * ro.y;

    float a = rd.x * rd.x + rd.z * rd.z - m * m * rd.y * rd.y;
    float b = ro.x * rd.x + ro.z * rd.z - m * rd.y * g;
    float c = ro.x * ro.x + ro.z * ro.z - g * g;

    if (abs(a) < TINY)
    {
        // Parallel to a generator: the quadratic degenerates to a line, so the inside is a
        // half-line.
        if (abs(b) < TINY)
        {
            return c < 0.0 ? spanOf(slabIn, slabOut) : noSpan();
        }

        float t = -0.5 * c / b;
        return b > 0.0
            ? spanOf(slabIn, min(t, slabOut))
            : spanOf(max(t, slabIn), slabOut);
    }

    float disc = b * b - a * c;
    if (disc < 0.0)
    {
        // No crossing at all: outside the double cone for the whole ray if it opens upward,
        // inside it for the whole ray if it opens downward.
        return a > 0.0 ? noSpan() : spanOf(slabIn, slabOut);
    }

    float s  = sqrt(disc);
    float t0 = min((-b - s) / a, (-b + s) / a);
    float t1 = max((-b - s) / a, (-b + s) / a);

    if (a > 0.0)
    {
        return spanOf(max(t0, slabIn), min(t1, slabOut));
    }

    float lower = min(t0, slabOut);
    return lower > slabIn
        ? spanOf(slabIn, lower)
        : spanOf(max(t1, slabIn), slabOut);
}

// Canonical half-space y <= 0, with its surface through the origin and its outward normal
// along +Y. The first primitive here with an end at infinity, which is why span ends carry a
// surface code at all rather than always naming the primitive.
Span planeSpan(vec3 ro, vec3 rd)
{
    if (abs(rd.y) < TINY)
    {
        return ro.y <= 0.0 ? spanOf(-INF, INF) : noSpan();
    }

    float t = -ro.y / rd.y;

    // Descending, the ray crosses into the solid and never leaves; ascending, it was inside
    // from the start.
    return rd.y < 0.0 ? spanOf(t, INF) : spanOf(-INF, t);
}

// Canonical torus: major radius 1 in the XZ plane, minor radius as given, Y through the hole.
//
//   (x^2 + y^2 + z^2 + 1 - minor^2)^2 = 4 (x^2 + z^2)
//
// Substituting the ray gives a quartic, and its four roots pair into the two spans a ray can
// cut from a ring. They are left in gRoots rather than pushed: the list they go into is a
// generated type, so the pairing belongs in the generated wrapper and the maths belongs here.
int torusRoots(vec3 ro, vec3 rd, float minor)
{
    gRoots[0] = 0.0;
    gRoots[1] = 0.0;
    gRoots[2] = 0.0;
    gRoots[3] = 0.0;

    float g = dot(rd, rd);
    if (g < TINY) return 0;

    // Re-origin at the ray's closest approach to the centre before forming the quartic. The
    // coefficients go as the fourth power of the origin's distance, so a camera ten units out
    // drags four orders of magnitude of cancellation into a 32-bit float. This costs one dot
    // product and removes it; without it a torus is visibly ragged from any distance.
    float shift = -dot(ro, rd) / g;
    vec3  o     = ro + shift * rd;

    float h = 2.0 * dot(o, rd);
    float i = dot(o, o) + 1.0 - minor * minor;

    float planar = rd.x * rd.x + rd.z * rd.z;
    float mixed  = o.x * rd.x + o.z * rd.z;
    float radial = o.x * o.x + o.z * o.z;

    float inv = 1.0 / (g * g);

    int count = solveQuartic(
        2.0 * g * h * inv,
        (h * h + 2.0 * g * i - 4.0 * planar) * inv,
        (2.0 * h * i - 8.0 * mixed) * inv,
        (i * i - 4.0 * radial) * inv);

    for (int k = 0; k < 4; ++k)
    {
        if (k >= count) break;
        gRoots[k] += shift;
    }

    return count;
}

// Even-odd point-in-contour test, by casting along +X and counting crossings.
//
// Used only when shading, to decide which way an edge's perpendicular points. The contour's
// winding is whatever the scene file wrote it as, and demanding one orientation would turn a
// counter-clockwise polygon into a solid rendered inside out, with no error to explain it.
bool insideContour(vec2 q, int offset, int edges)
{
    bool inside = false;

    for (int e = 0; e < edges; ++e)
    {
        vec4 edge = SHAPE(offset + e);
        vec2 a = edge.xy;
        vec2 b = edge.zw;

        if ((a.y > q.y) != (b.y > q.y))
        {
            float x = a.x + (q.y - a.y) / (b.y - a.y) * (b.x - a.x);
            if (q.x < x) inside = !inside;
        }
    }

    return inside;
}

// A sphere given explicitly, rather than the canonical one. The sweep needs both of its end
// caps, and neither is at the origin.
Span ballSpan(vec3 ro, vec3 rd, vec3 centre, float radius)
{
    vec3  d = ro - centre;
    float a = dot(rd, rd);
    float b = dot(d, rd);
    float c = dot(d, d) - radius * radius;

    float disc = b * b - a * c;
    if (disc < 0.0 || a < TINY) return noSpan();

    float s = sqrt(disc);
    return spanOf((-b - s) / a, (-b + s) / a);
}

// The convex hull of two spheres: a cap at each end and the cone tangent to both.
//
// Because the hull is CONVEX a ray meets it in exactly one interval, and that interval is
// simply the outermost of whatever the three pieces give. No merge is needed and no piece has
// to know about the others -- which is what makes this cheap enough to run per segment.
Span roundConeSpan(vec3 ro, vec3 rd, vec3 a, float ra, vec3 b, float rb)
{
    vec3  axis = b - a;
    float len2 = dot(axis, axis);
    float dr   = rb - ra;

    // One sphere swallows the other, and the hull is just the larger one. Left to the general
    // path this would be a cone with an imaginary half-angle.
    if (len2 <= dr * dr)
    {
        return ra >= rb ? ballSpan(ro, rd, a, ra) : ballSpan(ro, rd, b, rb);
    }

    float len  = sqrt(len2);
    vec3  u    = axis / len;
    float sinT = dr / len;
    float cosT = sqrt(max(0.0, 1.0 - sinT * sinT));

    // The tangent line touches each sphere OFF its centre, and that is where the frustum's
    // ends sit. A cone drawn between the two centres instead would cut into both caps -- the
    // classic way to get a sphere sweep visibly wrong at every joint.
    vec3  p0 = a + u * (-ra * sinT);
    vec3  p1 = a + u * (len - rb * sinT);
    float R0 = ra * cosT;
    float R1 = rb * cosT;

    float tIn  =  INF;
    float tOut = -INF;

    Span capA = ballSpan(ro, rd, a, ra);
    if (capA.tOut >= capA.tIn) { tIn = min(tIn, capA.tIn); tOut = max(tOut, capA.tOut); }

    Span capB = ballSpan(ro, rd, b, rb);
    if (capB.tOut >= capB.tIn) { tIn = min(tIn, capB.tIn); tOut = max(tOut, capB.tOut); }

    // The lateral band. Its two crossings are folded in as single points rather than as an
    // interval: the hull is convex, so min and max over everything is the answer either way,
    // and a root outside the band simply never gets folded.
    vec3  ba = p1 - p0;
    float h  = length(ba);

    if (h > TINY)
    {
        vec3  n  = ba / h;
        float k  = (R1 - R0) / h;
        float q  = 1.0 + k * k;

        vec3  w  = ro - p0;
        float h0 = dot(w, n);
        float h1 = dot(rd, n);

        // radial^2 - (R0 + k*axial)^2 == 0, with radial^2 = |w|^2 - axial^2.
        float ca = dot(rd, rd) - q * h1 * h1;
        float cb = dot(w, rd) - q * h0 * h1 - R0 * k * h1;
        float cc = dot(w, w) - q * h0 * h0 - 2.0 * R0 * k * h0 - R0 * R0;

        float disc = cb * cb - ca * cc;

        if (abs(ca) > TINY && disc >= 0.0)
        {
            float s  = sqrt(disc);
            float t0 = (-cb - s) / ca;
            float t1 = (-cb + s) / ca;

            for (int i = 0; i < 2; ++i)
            {
                float t = i == 0 ? t0 : t1;
                float axial = h0 + h1 * t;

                if (axial < 0.0 || axial > h) continue;   // past one of the tangent circles

                tIn  = min(tIn, t);
                tOut = max(tOut, t);
            }
        }
    }

    return tIn <= tOut ? spanOf(tIn, tOut) : noSpan();
}

// --- Scene access ----------------------------------------------------------------------

// The world-to-local matrix of a primitive.
//
// mat4's constructor takes COLUMNS. The CPU wrote the ROWS of a System.Numerics matrix,
// which is row-vector and row-major, so passing them here builds its transpose -- and the
// transpose of a row-vector matrix is exactly the column-vector matrix for the same
// transform. This function is the only place that convention is defined on the buffer
// path; there is no transpose() to add anywhere else.
mat4 fetchMatrix(int base)
{
    return mat4(
        PRIMITIVE(base + 1),
        PRIMITIVE(base + 2),
        PRIMITIVE(base + 3),
        PRIMITIVE(base + 4));
}

// The perpendicular to whichever edge of a contour the point lies on, pointing outward.
//
// Shared by the prism and the lathe: one works in XZ and the other in the (radius, y)
// half-plane, but the question -- which edge, and which way does it face -- is the same, and
// so is the answer.
vec2 edgeNormal(int offset, int edges, int e)
{
    vec4 edge = SHAPE(offset + ((e % edges) + edges) % edges);
    vec2 s = edge.zw - edge.xy;
    return normalize(vec2(s.y, -s.x));
}

// `smooth_` blends the perpendicular with the neighbouring edge's across each joint, which is
// what makes a tessellated curve read as a curve. The silhouette is smooth at any step count,
// but the SHADING facets stay visible however fine the tessellation -- a Bézier vase without
// this comes out looking like a stack of rings. It is off for a hand-written outline, whose
// corners are meant to be corners.
vec2 contourNormal(vec2 q, int offset, int edges, bool smooth_)
{
    float best   = INF;
    vec2  normal = vec2(1.0, 0.0);

    for (int e = 0; e < edges; ++e)
    {
        vec4 edge = SHAPE(offset + e);
        vec2 a = edge.xy;
        vec2 s = edge.zw - a;

        float len2 = dot(s, s);
        float u    = len2 < TINY ? 0.0 : clamp(dot(q - a, s) / len2, 0.0, 1.0);
        vec2  foot = a + u * s;
        float d    = dot(q - foot, q - foot);

        if (d >= best) continue;

        best   = d;
        normal = normalize(vec2(s.y, -s.x));

        if (!smooth_) continue;

        // Half a joint's worth of the neighbour on each side: at the shared vertex the two
        // perpendiculars weigh equally, and at the middle of an edge the edge's own wins
        // outright. Every edge's perpendicular is built by the same formula, so a consistently
        // wound contour has them all facing the same way and blending is safe.
        if (u < 0.5) normal = normalize(mix(normal, edgeNormal(offset, edges, e - 1), 0.5 - u));
        else         normal = normalize(mix(normal, edgeNormal(offset, edges, e + 1), u - 0.5));
    }

    // The perpendicular could point either way, since the contour's winding is whatever the
    // file wrote. One point-in-contour test settles it, and it runs once per shaded pixel
    // rather than once per span.
    return insideContour(q + normal * 10.0 * EPS, offset, edges) ? -normal : normal;
}

// The outward normal of one round cone at p, with `away` set to how far p is from its
// surface so the caller can tell which segment of a sweep the point actually belongs to.
//
// The whole of a swept sphere's shading rests on one fact: at a surface point the outward
// normal is the direction from the centre of the generating sphere that TOUCHES there. On the
// caps that sphere is an end one; on the band it is an interior one, and the normal tilts off
// radial by exactly the cone's half-angle. Using the radial direction alone is the usual
// mistake, and it shows as a tube lit as though it were a cylinder however much it tapers.
vec3 roundConeNormal(vec3 p, vec3 a, float ra, vec3 b, float rb, out float away)
{
    vec3  axis = b - a;
    float len2 = dot(axis, axis);
    float dr   = rb - ra;

    if (len2 <= dr * dr)
    {
        vec3  centre = ra >= rb ? a : b;
        float radius = max(ra, rb);
        vec3  d      = p - centre;
        float l      = length(d);

        away = abs(l - radius);
        return l > TINY ? d / l : vec3(0.0, 1.0, 0.0);
    }

    float len  = sqrt(len2);
    vec3  u    = axis / len;
    float sinT = dr / len;
    float cosT = sqrt(max(0.0, 1.0 - sinT * sinT));

    float axialA = dot(p - a, u);

    // The band runs between the two tangent circles, not between the two centres.
    if (axialA < -ra * sinT)
    {
        vec3  d = p - a;
        float l = length(d);
        away = abs(l - ra);
        return l > TINY ? d / l : vec3(0.0, 1.0, 0.0);
    }

    if (axialA > len - rb * sinT)
    {
        vec3  d = p - b;
        float l = length(d);
        away = abs(l - rb);
        return l > TINY ? d / l : vec3(0.0, 1.0, 0.0);
    }

    vec3  radial = (p - a) - u * axialA;
    float rl     = length(radial);
    vec3  rhat   = rl > TINY ? radial / rl : vec3(0.0);

    // Radius of the lateral surface at this axial position, measured from a's tangent circle.
    float surface = (ra * cosT) + ((rb - ra) * cosT / len) * (axialA + ra * sinT);
    away = abs(rl - surface) * cosT;

    return normalize(rhat * cosT - u * sinT);
}

vec3 primitiveNormal(int kind, float pa, float pb, vec3 p)
{
    if (kind == KIND_SPHERE)
    {
        return normalize(p);
    }

    if (kind == KIND_BOX)
    {
        // The hit lies on a face, so the dominant coordinate names that face.
        vec3 a = abs(p);
        if (a.x >= a.y && a.x >= a.z) return vec3(sign(p.x), 0.0, 0.0);
        if (a.y >= a.z)               return vec3(0.0, sign(p.y), 0.0);
        return vec3(0.0, 0.0, sign(p.z));
    }

    if (kind == KIND_CYLINDER)
    {
        if (p.y < EPS)       return vec3(0.0, -1.0, 0.0);
        if (p.y > 1.0 - EPS) return vec3(0.0,  1.0, 0.0);
        return normalize(vec3(p.x, 0.0, p.z));
    }

    if (kind == KIND_CONE)
    {
        if (p.y < EPS)       return vec3(0.0, -1.0, 0.0);
        if (p.y > 1.0 - EPS) return vec3(0.0,  1.0, 0.0);

        // Gradient of x^2 + z^2 - (1 + m y)^2. The Y component is what tilts the normal off
        // the radial direction, and it vanishes at m = 0 -- where the cone is a cylinder.
        float m = pa - 1.0;
        return normalize(vec3(p.x, -m * (1.0 + m * p.y), p.z));
    }

    if (kind == KIND_PLANE)
    {
        return vec3(0.0, 1.0, 0.0);
    }

    if (kind == KIND_TORUS)
    {
        // The nearest point on the ring's centre circle, which has radius 1 here. The normal
        // runs from there to the hit point.
        float len = length(p.xz);
        vec3  ring = len > TINY ? vec3(p.x / len, 0.0, p.z / len) : vec3(1.0, 0.0, 0.0);
        return normalize(p - ring);
    }

    if (kind == KIND_PRISM)
    {
        if (p.y < EPS)       return vec3(0.0, -1.0, 0.0);
        if (p.y > 1.0 - EPS) return vec3(0.0,  1.0, 0.0);

        vec2 n = contourNormal(p.xz, int(pa), int(pb), false);
        return vec3(n.x, 0.0, n.y);
    }

    if (kind == KIND_LATHE)
    {
        // A negative count is the flag for an outline that came from a curve; see
        // CsgTapeBuilder.VisitLathe for why the sign carries it.
        int   edges = int(abs(pb));
        float rho   = length(p.xz);
        vec2  n     = contourNormal(vec2(rho, p.y), int(pa), edges, pb < 0.0);

        // Lift out of the half-plane: the radial component follows the hit point round the
        // axis, the axial one is already in world terms.
        vec2 radial = rho > TINY ? p.xz / rho : vec2(1.0, 0.0);
        return normalize(vec3(radial.x * n.x, n.y, radial.y * n.x));
    }

    if (kind == KIND_SWEEP)
    {
        int   offset  = int(pa);
        int   spheres = int(pb);
        vec3  best    = vec3(0.0, 1.0, 0.0);
        float nearest = INF;

        // The hit lies on the surface of exactly one segment and inside any others it
        // overlaps, so the segment whose surface p is closest to is the one that owns it.
        for (int i = 0; i + 1 < spheres; ++i)
        {
            vec4 s0 = SHAPE(offset + i);
            vec4 s1 = SHAPE(offset + i + 1);

            float away;
            vec3  n = roundConeNormal(p, s0.xyz, s0.w, s1.xyz, s1.w, away);

            if (away < nearest)
            {
                nearest = away;
                best    = n;
            }
        }

        return best;
    }

    // Blob. Its surface is a level set, so the normal is the field's gradient -- and the
    // field rises towards the inside, so the outward normal runs against it.
    int    offset     = int(pa);
    int    components = int(pb);
    vec3   gradient   = vec3(0.0);

    for (int i = 0; i < components; ++i)
    {
        vec4  ball     = SHAPE(offset + 1 + 2 * i);
        float strength = SHAPE(offset + 2 + 2 * i).x;

        vec3  d  = p - ball.xyz;
        float r2 = ball.w * ball.w;
        float d2 = dot(d, d);

        if (d2 >= r2) continue;

        // d/dp of strength * (1 - d2/r2)^2.
        gradient += (-4.0 * strength * (1.0 - d2 / r2) / r2) * d;
    }

    float len = length(gradient);
    return len < TINY ? vec3(0.0, 1.0, 0.0) : -gradient / len;
}

// --- Tracing ---------------------------------------------------------------------------

struct Hit
{
    bool  found;
    float t;
    int   primitive;
    bool  flip;

    // Which end of the span was hit: true at tIn, false at tOut. Refraction needs it to
    // know which way to apply the index of refraction, and this is where the interval
    // algorithm pays off a second time -- the answer is already in the span, where a mesh
    // renderer has to infer it from a normal and gets it wrong on an open mesh.
    bool  entering;
};

Hit noHit()
{
    Hit h;
    h.found     = false;
    h.t         = INF;
    h.primitive = 0;
    h.flip      = false;
    h.entering  = true;
    return h;
}

// Does the ray meet the box, anywhere before `limit`?
//
// The box arrives as two constants rather than as an offset into a buffer, and the guard is a
// plain `if` around a root rather than a jump instruction inside a tape -- so it costs nothing
// to have and nothing to skip, and a root that is unbounded (one holding a `plane`) simply
// gets no call at all.
//
// The slab test, with one difference from boxSpan's: the reciprocal is clamped rather than
// taken raw. A ray exactly parallel to a slab gives rd = 0, and 1/0 is infinity, and
// (lo - ro) * infinity is NaN whenever the ray also LIES IN that plane -- which for a
// world-space box around a wall is not an exotic case, it is what a ray grazing the floor
// does. Every comparison against a NaN is false, so the box would be missed, and the wall
// would develop a seam. Clamping the magnitude and keeping the sign gives a number large
// enough to behave like infinity and finite enough to multiply by zero.
bool boundHit(vec3 ro, vec3 rd, vec3 lo, vec3 hi, float limit)
{
    vec3 sign_ = vec3(rd.x >= 0.0 ? 1.0 : -1.0, rd.y >= 0.0 ? 1.0 : -1.0, rd.z >= 0.0 ? 1.0 : -1.0);
    vec3 inv   = sign_ / max(abs(rd), vec3(TINY));

    vec3 t1 = (lo - ro) * inv;
    vec3 t2 = (hi - ro) * inv;

    vec3 near = min(t1, t2);
    vec3 far  = max(t1, t2);

    float tIn  = max(near.x, max(near.y, near.z));
    float tOut = min(far.x, min(far.y, far.z));

    // tOut >= EPS rather than >= tIn alone: a box entirely behind the eye is missed, matching
    // resolveRoot, which discards a span for the same reason. `limit` carries whichever is
    // nearer of the shadow ray's target and the closest surface found so far -- a subtree that
    // begins beyond a hit already recorded cannot produce a nearer one.
    return tOut >= max(tIn, EPS) && tIn <= limit;
}

// =========================================================================================
// @chroma:geometry
//
// The scene goes here. Everything above is scene-independent and hand-written; everything the
// host splices in at this marker was generated by Chroma.Core.Codegen for one scene and no
// other -- the span-list types, the boolean operators at the sizes this scene needs, one
// function per leaf carrying its transform and its outline as constants, one per root, and
// traceScene() below them.
//
// There is deliberately no `trace` and no `occluded` wrapper. Each one is a call site, and a
// call site is a COPY: the compiler inlines the whole scene walk into every place it is called
// from, and iteration 7 established that this is what puts the shader one step from "cannot
// locate suitable resource to bind variable". So callers use traceScene directly, and `anyHit`
// is passed as a value rather than baked in by the site.
// =========================================================================================

vec3 hitNormal(Hit hit, vec3 point)
{
    int  base    = hit.primitive * PRIMITIVE_TEXELS;
    vec4 header  = PRIMITIVE(base);
    mat4 toLocal = fetchMatrix(base);

    vec3 local  = (toLocal * vec4(point, 1.0)).xyz;
    vec3 normal = primitiveNormal(int(header.x), header.z, header.w, local);

    // Normals transform by the inverse transpose. toLocal already IS the inverse, so its
    // transpose is the normal matrix. Using mat3(toLocal) instead agrees for pure
    // rotations, which is why that mistake survives every test scene without a scale.
    normal = normalize(transpose(mat3(toLocal)) * normal);

    return hit.flip ? -normal : normal;
}

// --- Randomness ------------------------------------------------------------------------

// PCG. Small, fast, and free of the visible structure that fract(sin(dot(...))) produces.
//
// GLSL has no global mutable state, so the seed travels as `inout` through every function
// that draws a number. Copying it instead of advancing it gives correlated noise, which
// does not look like noise -- it looks like banding, and reads as a geometry bug.
uint pcg(inout uint state)
{
    state = state * 747796405u + 2891336453u;
    uint word = ((state >> ((state >> 28u) + 4u)) ^ state) * 277803737u;
    return (word >> 22u) ^ word;
}

float rand(inout uint state)
{
    return float(pcg(state)) * (1.0 / 4294967296.0);
}

vec2 rand2(inout uint state)
{
    return vec2(rand(state), rand(state));
}

// Pixel and frame hashed together, not concatenated: neighbouring pixels must not start
// from neighbouring states, or the first frames show visible structure.
uint seedFor(ivec2 pixel, int sample_)
{
    uint state = uint(pixel.x) * 1973u + uint(pixel.y) * 9277u + uint(sample_) * 26699u;
    pcg(state);
    return state;
}

// --- Materials -------------------------------------------------------------------------

struct Material
{
    vec3  albedo;      // base colour
    float roughness;
    vec3  emission;
    float metallic;
    vec3  absorption;  // sigma_a, per world unit, inside the solid
    float transmission;
    float ior;
    float scattering;  // sigma_s, per world unit -- scalar, see documents/transparency.md
    float anisotropy;  // Henyey-Greenstein g; > 0 scatters forward
};

Material fetchMaterial(int primitive)
{
    int index = int(PRIMITIVE(primitive * PRIMITIVE_TEXELS).y);

    vec4 first  = MATERIAL(index * MATERIAL_TEXELS);
    vec4 second = MATERIAL(index * MATERIAL_TEXELS + 1);
    vec4 third  = MATERIAL(index * MATERIAL_TEXELS + 2);
    vec4 fourth = MATERIAL(index * MATERIAL_TEXELS + 3);

    return Material(
        first.rgb, first.a,
        second.rgb, second.a,
        third.rgb, third.a,
        fourth.x, fourth.y, fourth.z);
}

// Extinction: the rate at which a beam loses radiance, by absorption or by being scattered
// out of it. This is what a *transmittance* needs -- the throughput of a path that scatters
// carries only sigma_a, because sigma_s cancels against the density the scattering point was
// drawn from. Getting the two the wrong way round darkens fog by exactly its albedo.
vec3 extinctionOf(Material m) { return m.absorption + vec3(m.scattering); }

// A metal has no diffuse lobe at all: free electrons absorb whatever is not reflected, so
// nothing scatters back out. That is what the (1 - metallic) factor encodes.
vec3 diffuseAlbedo(Material m) { return m.albedo * (1.0 - m.metallic); }

float alphaOf(Material m) { return max(m.roughness * m.roughness, MIN_ALPHA); }

float luminance(vec3 c) { return dot(c, vec3(0.2126, 0.7152, 0.0722)); }

// The relative index of refraction across the boundary just hit. Entering, the ray goes
// from vacuum into the solid; leaving, the other way. hitNormal always returns a normal
// facing the ray, so this one number is all that distinguishes the two cases.
float relativeIor(Material m, bool entering)
{
    return entering ? 1.0 / m.ior : m.ior;
}

// --- Sampling helpers --------------------------------------------------------------------

// Branchless orthonormal basis around a unit vector (Duff et al.). The sign trick is what
// keeps it stable when n is near -Z, where the naive cross-product construction collapses.
void basisFrom(vec3 n, out vec3 t, out vec3 b)
{
    float s = n.z >= 0.0 ? 1.0 : -1.0;
    float a = -1.0 / (s + n.z);
    float c = n.x * n.y * a;

    t = vec3(1.0 + s * n.x * n.x * a, s * c, -s * n.x);
    b = vec3(c, s + n.y * n.y * a, -n.y);
}

vec3 toWorld(vec3 n, vec3 local)
{
    vec3 t, b;
    basisFrom(n, t, b);
    return normalize(t * local.x + b * local.y + n * local.z);
}

// Cosine-weighted, so grazing directions -- which the cosine term barely uses -- are drawn
// rarely. The payoff is that the pdf cancels the cosine and the 1/PI exactly.
vec3 cosineHemisphere(vec3 n, vec2 u)
{
    float r   = sqrt(u.x);
    float phi = 2.0 * PI * u.y;

    return toWorld(n, vec3(r * cos(phi), r * sin(phi), sqrt(max(0.0, 1.0 - u.x))));
}

// A microfacet normal drawn from the GGX distribution.
vec3 sampleGgxHalf(vec3 n, float alpha, vec2 u)
{
    float phi      = 2.0 * PI * u.x;
    float cosTheta = sqrt((1.0 - u.y) / (1.0 + (alpha * alpha - 1.0) * u.y));
    float sinTheta = sqrt(max(0.0, 1.0 - cosTheta * cosTheta));

    return toWorld(n, vec3(sinTheta * cos(phi), sinTheta * sin(phi), cosTheta));
}

// A direction uniformly inside the cone a sphere subtends, which is exact and wastes none
// of the samples that picking points on the whole sphere would put on its far side.
vec3 sampleCone(vec3 axis, float cosMax, vec2 u)
{
    float cosTheta = 1.0 - u.x * (1.0 - cosMax);
    float sinTheta = sqrt(max(0.0, 1.0 - cosTheta * cosTheta));
    float phi      = 2.0 * PI * u.y;

    return toWorld(axis, vec3(sinTheta * cos(phi), sinTheta * sin(phi), cosTheta));
}

// --- The phase function: Henyey-Greenstein ------------------------------------------------
//
// The volumetric counterpart of the BRDF, and simpler than one: there is no surface, so no
// normal, no hemisphere and no cosine factor. It is a density over the whole sphere.
//
// THE CONVENTION, because it is a sign and nothing in a still image reveals it: `mu` is the
// cosine between the direction the light is TRAVELLING and the direction it leaves in. So
// g > 0 is forward scattering -- haze, smoke, a visible beam -- and g < 0 is backward.
// Implementations that keep a vector pointing back towards the previous vertex, as PBRT
// does, carry the opposite sign throughout.

float phaseHg(float mu, float g)
{
    float gg = g * g;
    float d  = max(1.0 + gg - 2.0 * g * mu, TINY);

    // d^(3/2), written as d * sqrt(d) because pow() of a near-zero base is where the forward
    // peak of a strongly anisotropic medium loses its precision.
    return (1.0 - gg) / (4.0 * PI * d * sqrt(d));
}

// Drawn from `phaseHg` exactly, so the weight it would contribute is 1 and no ratio survives
// into the throughput -- the same cancellation the cosine-weighted hemisphere gets.
vec3 samplePhaseHg(vec3 forward, float g, vec2 u)
{
    float mu;

    if (abs(g) < 1e-3)
    {
        mu = 1.0 - 2.0 * u.x;   // the isotropic limit, where the expression below divides by 0
    }
    else
    {
        float s = (1.0 - g * g) / (1.0 - g + 2.0 * g * u.x);
        mu = (1.0 + g * g - s * s) / (2.0 * g);
    }

    float sinTheta = sqrt(max(0.0, 1.0 - mu * mu));
    float phi      = 2.0 * PI * u.y;

    return toWorld(forward, vec3(sinTheta * cos(phi), sinTheta * sin(phi), mu));
}

// --- The BRDF: Lambert + Cook-Torrance GGX -----------------------------------------------

float distributionGgx(float nDotH, float alpha)
{
    float a2 = alpha * alpha;
    float d  = nDotH * nDotH * (a2 - 1.0) + 1.0;
    return a2 / (PI * d * d);
}

// Smith's masking-shadowing term, one factor per direction.
float smithG1(float nDotX, float alpha)
{
    float a2 = alpha * alpha;
    return 2.0 * nDotX / (nDotX + sqrt(a2 + (1.0 - a2) * nDotX * nDotX));
}

vec3 fresnelSchlick(vec3 f0, float vDotH)
{
    float f = 1.0 - vDotH;
    float f2 = f * f;
    return f0 + (1.0 - f0) * (f2 * f2 * f);
}

// Reflectance of a dielectric boundary at normal incidence. Symmetric under eta -> 1/eta,
// so a boundary has one value whichever way it is crossed. At ior 1.5 it gives 0.04, which
// is the constant iteration 4 wrote by hand for every dielectric.
float dielectricF0(float eta)
{
    float r = (eta - 1.0) / (eta + 1.0);
    return r * r;
}

// Returns exactly 1.0 under total internal reflection, so callers can test for it without a
// second Snell evaluation.
float fresnelDielectric(float cosThetaI, float eta)
{
    float sinT2 = eta * eta * (1.0 - cosThetaI * cosThetaI);

    if (sinT2 >= 1.0)
    {
        return 1.0;   // no transmitted direction exists
    }

    // Schlick is derived for light arriving from the thinner medium. Fed the incident angle
    // from the dense side it reaches 1 only at 90 degrees, where the true reflectance
    // reaches 1 at the critical angle -- 41.8 degrees for glass. Using the transmitted angle
    // makes the two agree. The symptom of getting this wrong is a dark rim on a glass
    // silhouette, which reads as an absorption bug.
    float cosine = eta > 1.0 ? sqrt(1.0 - sinT2) : cosThetaI;

    float f  = 1.0 - cosine;
    float f2 = f * f;
    float f0 = dielectricF0(eta);

    return f0 + (1.0 - f0) * (f2 * f2 * f);
}

// One Fresnel term for the whole material: the dielectric response for the dielectric part,
// the tinted metallic one for the rest.
//
// Schlick is affine in F0, so blending the two results by `metallic` is identical to
// blending their F0 values -- which is exactly what iteration 4 did with mix(0.04, colour,
// metallic). That identity is why adding `ior` changed no existing image.
vec3 fresnelOf(Material m, float vDotH, float eta)
{
    return mix(
        vec3(fresnelDielectric(vDotH, eta)),
        fresnelSchlick(m.albedo, vDotH),
        m.metallic);
}

// The BRDF for a known pair of directions, both above the surface. Needed by light
// sampling, where the direction is chosen by the light rather than by the surface.
//
// The transmission lobe is deliberately absent: for a smooth dielectric it is a delta and
// contributes nothing to a light sample. The consequence is that a rough transmissive
// surface gets no direct light *through* itself, only through the bounce loop -- see
// documents/transparency.md, "Limits".
vec3 evalBrdf(Material m, vec3 n, vec3 v, vec3 l, float eta)
{
    float nDotL = dot(n, l);
    float nDotV = dot(n, v);

    if (nDotL <= 0.0 || nDotV <= 0.0)
    {
        return vec3(0.0);
    }

    vec3  h     = normalize(v + l);
    float nDotH = max(dot(n, h), 0.0);
    float vDotH = max(dot(v, h), 0.0);

    float alpha = alphaOf(m);
    vec3  f     = fresnelOf(m, vDotH, eta);
    float g     = smithG1(nDotL, alpha) * smithG1(nDotV, alpha);
    float d     = distributionGgx(nDotH, alpha);

    vec3 specular = (d * g) * f / max(4.0 * nDotV * nDotL, TINY);

    // (1 - F) is what stops the diffuse and specular lobes together reflecting more than
    // arrived. Dropping it is a common shortcut and shows up as surfaces that glow at
    // grazing angles.
    //
    // (1 - transmission) is the same accounting one level up: what passes through is not
    // available to scatter. This factor and the sampler's must agree exactly, and they are
    // a hundred lines apart -- which is how that bug survives.
    vec3 diffuse = diffuseAlbedo(m) * (1.0 - m.transmission) * (vec3(1.0) - f) / PI;

    return diffuse + specular;
}

// Draws an outgoing direction and returns f * cos / pdf for it -- already divided by the
// probability of having picked this lobe. False means the path ends here.
//
// Three lobes, chosen from one microfacet:
//
//   BSDF = specular reflection
//        + transmission       * specular transmission     (Walter et al. 2007)
//        + (1 - transmission) * diffuse
//
// The reflection and transmission weights collapse to the SAME expression, differing only
// by F against (1 - F): D, the eta factors, the refraction Jacobian and its denominator all
// cancel against the pdf. If D or eta still appears in a weight below, the derivation was
// not carried through -- documents/transparency.md spells it out.
bool sampleBrdf(
    Material m, vec3 n, vec3 v, float eta, inout uint seed,
    out vec3 dir, out vec3 weight, out bool transmitted)
{
    dir         = n;
    weight      = vec3(0.0);
    transmitted = false;

    float alpha = alphaOf(m);
    vec3  h     = sampleGgxHalf(n, alpha, rand2(seed));

    float nDotV = max(dot(n, v), TINY);
    float nDotH = max(dot(n, h), TINY);

    // A microfacet turned away from the viewer is not visible, so neither specular lobe
    // exists through it. This must NOT end the path: the diffuse lobe does not go through
    // `h` at all, and killing it here throws away most of a matte surface's samples --
    // measured at 7% of cornell.chroma's overall brightness when it was wrong, and 12% on
    // the floor.
    float vDotH   = dot(v, h);
    bool  visible = vDotH > 0.0;

    vec3  f           = vec3(0.0);
    float pReflect    = 0.0;

    if (visible)
    {
        float fDielectric = fresnelDielectric(vDotH, eta);
        f = mix(vec3(fDielectric), fresnelSchlick(m.albedo, vDotH), m.metallic);

        // The probability *is* the energy split rather than a heuristic standing in for it:
        // for an untinted F the ratio F / pReflect below is 1 and the weight reduces to the
        // geometry term alone. Clamped so neither branch is ever unreachable, which would
        // bias the result however many samples are taken -- except under total internal
        // reflection, where nothing is transmitted and the other branch would spend a
        // twentieth of the samples computing exact zeros.
        pReflect = fDielectric >= 1.0 ? 1.0 : clamp(luminance(f), 0.05, 0.95);
    }

    // Letting the lobe probability depend on the microfacet already drawn is legitimate:
    // the weight divides by the *conditional* probability, so the estimate stays unbiased
    // whatever h turned out to be.
    if (rand(seed) < pReflect)
    {
        dir = reflect(-v, h);

        float nDotL = dot(n, dir);
        if (nDotL <= 0.0)
        {
            return false;   // the sample went below the surface
        }

        float g = smithG1(nDotL, alpha) * smithG1(nDotV, alpha);
        weight  = (f * g * vDotH) / (nDotV * nDotH * pReflect);
        return true;
    }

    // Whatever was not reflected: through the surface, or scattered off it. The
    // `transmission` and `1 - transmission` factors of the BSDF cancel exactly against
    // these two probabilities, which is why neither appears in a weight.
    if (rand(seed) < m.transmission)
    {
        if (!visible)
        {
            return false;   // no facet to refract through; the sample contributes nothing
        }

        dir = refract(-v, h, eta);

        // refract() returns the zero vector under total internal reflection rather than
        // signalling it, so a caller that does not test ends up tracing a ray with no
        // direction.
        if (dot(dir, dir) < TINY || dot(n, dir) >= 0.0)
        {
            return false;
        }

        float g = smithG1(abs(dot(n, dir)), alpha) * smithG1(nDotV, alpha);
        weight  = ((vec3(1.0) - f) * g * vDotH) / (nDotV * nDotH * (1.0 - pReflect));
        transmitted = true;
        return true;
    }

    dir = cosineHemisphere(n, rand2(seed));

    if (dot(n, dir) <= 0.0)
    {
        return false;
    }

    // The half-vector is recomputed here, and it matters. The `h` drawn from D belongs to
    // the two specular lobes; the diffuse lobe has to be *evaluated* at the direction
    // actually sampled, or the estimator stops being unbiased. Using h to *choose* a lobe
    // is fine -- a probability may depend on anything already known.
    vec3 hDiffuse = normalize(v + dir);
    vec3 fDiffuse = fresnelOf(m, max(dot(v, hDiffuse), 0.0), eta);

    // The cosine and the 1/PI cancel against the pdf; only the albedo and (1 - F) stay.
    weight = diffuseAlbedo(m) * (vec3(1.0) - fDiffuse) / (1.0 - pReflect);
    return true;
}

// --- Shadow rays -------------------------------------------------------------------------

// How much of a light survives the trip to `ro`. Opaque scenes get a boolean answer; through
// glass the honest answer is a colour.
//
// The ray does NOT bend at each boundary. A refracted shadow ray would no longer point at
// the light, so the question "is the light visible along this ray" would stop being
// answerable this way. That approximation is exactly why the shadow under a glass sphere is
// an evenly dimmed disc with no bright spot: a caustic can only arrive through the bounce
// loop.
//
// `startMedium` is the extinction the origin is already sitting in, zero when it is in
// vacuum. Every caller before iteration 10 passed nothing and got zero, which was wrong in a
// way nothing showed: a point inside glass -- or anywhere at all inside fog -- has medium
// between it and the light before the first boundary is reached. No companion flag is needed,
// because being inside a medium of zero extinction and not being inside one at all are the
// same computation all the way down.
vec3 shadowTransmittance(vec3 ro, vec3 rd, float maxT, vec3 startMedium)
{
#if !CHROMA_TRANSMISSION

    // An opaque scene asks a different question -- "is anything in the way at all" -- and the
    // walk answers it by returning at the first thing it meets. Nothing below this point is
    // compiled for such a scene: not the loop, not the medium bookkeeping, and not the second
    // and third tape walks the loop would otherwise carry.
    return traceScene(ro, rd, true, maxT).found ? vec3(0.0) : vec3(1.0);

#else

    vec3  transmittance = vec3(1.0);
    vec3  medium        = startMedium;
    bool  inMedium      = true;
    float remaining     = maxT;

    for (int step = 0; step < MAX_SHADOW_STEPS; ++step)
    {
        Hit hit = traceScene(ro, rd, false, remaining);

        if (!hit.found || hit.t >= remaining)
        {
            // Nothing else in the way. If the ray is still inside a solid then so is the
            // light, so attenuate over what is left of the distance.
            if (inMedium)
            {
                transmittance *= exp(-medium * remaining);
            }

            return transmittance;
        }

        if (inMedium)
        {
            transmittance *= exp(-medium * hit.t);
        }

        Material m = fetchMaterial(hit.primitive);

        if (m.transmission <= 0.0)
        {
            return vec3(0.0);
        }

        transmittance *= m.transmission;

        if (dot(transmittance, transmittance) < TINY)
        {
            return vec3(0.0);
        }

        inMedium = hit.entering;
        medium   = extinctionOf(m);

        // rd is a unit vector for every light shape, so t and distance are the same thing.
        float advance = hit.t + SHADOW_BIAS;
        ro        += rd * advance;
        remaining -= advance;
    }

    // Out of steps. Returning what was gathered rather than zero: under-occluding shows up
    // as a slightly bright patch, over-occluding as a black band with no visible cause.
    return transmittance;

#endif
}

// --- Direct lighting -------------------------------------------------------------------

// One sample of light `i` as seen from `p`: the direction to it, the radiance arriving
// already divided by its pdf, and how far a shadow ray may travel. False when the light
// contributes nothing from here.
//
// Split out of directLight so the volumetric estimator below can reuse it verbatim. The two
// differ in what they do with a sample, never in how one is drawn, so a light shape added
// here reaches both.
bool sampleLight(int i, vec3 p, inout uint seed, out vec3 toLight, out vec3 arriving, out float maxT)
{
    if (uLightKind[i] == 1)
    {
        toLight  = -uLightVector[i];   // stored as the direction the light travels
        arriving = uLightColor[i];     // infinitely far away, so no falloff
        maxT     = INF;
        return true;
    }

    vec3  delta  = uLightVector[i] - p;
    float distSq = dot(delta, delta);
    float dist   = sqrt(distSq);
    float radius = uLightRadius[i];

    if (radius <= 0.0)
    {
        toLight = delta / dist;
        maxT    = dist;

        // Inverse-square falloff. Iterations 2 and 3 omitted it, which made brightness
        // independent of distance -- tolerable for a direct-lighting toy, incoherent the
        // moment energy has to balance across bounces.
        arriving = uLightColor[i] / distSq;
        return true;
    }

    if (dist <= radius)
    {
        return false;   // the shading point is inside the light; there is no cone
    }

    float cosMax = sqrt(max(0.0, 1.0 - (radius * radius) / distSq));
    toLight = sampleCone(delta / dist, cosMax, rand2(seed));

    // The sphere's radiance is intensity/(PI r^2) and 1/pdf is 2 PI (1 - cosMax), so the two
    // PIs cancel. That normalisation is what makes `radius` a pure softness control: as it
    // shrinks, this tends to intensity/dist^2 exactly.
    arriving = uLightColor[i] * (2.0 * (1.0 - cosMax) / (radius * radius));

    // Stop at the sphere's near surface, not at its centre, or its far half shadows its
    // near half.
    float proj = dot(delta, toLight);
    float disc = max(0.0, radius * radius - (distSq - proj * proj));
    maxT = proj - sqrt(disc);
    return true;
}

// Radiance reaching `p` directly from the declared lights.
//
// Sampling the lights explicitly, rather than waiting for a bounced ray to find one, is
// what makes the image converge in seconds instead of never: an idealised point light has
// zero area and would never be hit at all.
//
// ONE function for two kinds of vertex, and the reason is not elegance. A scattering point
// needs the same estimator with the cosine and the BRDF replaced by a phase function, which
// reads as a second function -- and a second function calls shadowTransmittance from a second
// place, the compiler inlines the tape walk into both, and the shader stops linking with
// "cannot locate suitable resource to bind variable". Iteration 7 found that ceiling from the
// other side. Keeping one call site is what buys the whole feature its room.
//
// `axis` is the surface normal when `onSurface`, and the direction the light is travelling
// when it is not. `medium` is the extinction the vertex sits in, zero in vacuum: a floor
// inside fog is lit *through* fog, and that attenuation is where a shaft gets its contrast.
vec3 directLight(
    vec3 p, vec3 axis, vec3 v, Material m, float eta,
    bool onSurface, float g, vec3 medium, inout uint seed)
{
    vec3 result = vec3(0.0);

    // On a surface, offset along the normal rather than along the ray: inside a cavity the
    // normal points into the hollow, which is exactly the side the shadow ray has to start
    // on. At a scattering point there is no surface to escape from, so there is no offset --
    // the medium's own boundary is still ahead of the ray and the walk below crosses it.
    vec3 origin = onSurface ? p + axis * SHADOW_BIAS : p;

    for (int i = 0; i < uLightCount; ++i)
    {
        vec3  toLight;
        vec3  arriving;
        float maxT;

        if (!sampleLight(i, p, seed, toLight, arriving, maxT))
        {
            continue;
        }

        vec3 weight;

        if (onSurface)
        {
            float cosTheta = dot(axis, toLight);
            if (cosTheta <= 0.0)
            {
                continue;
            }

            weight = evalBrdf(m, axis, v, toLight, eta) * cosTheta;
        }
        else
        {
            // No cosine: there is no normal. The phase function is *evaluated* towards the
            // light here, never sampled. And no single-scattering albedo -- sigma_s already
            // cancelled against the density the scattering point was drawn from, so applying
            // it again would darken every medium by exactly its albedo.
            weight = vec3(phaseHg(dot(axis, toLight), g));
        }

        if (dot(weight, weight) <= 0.0)
        {
            continue;
        }

        vec3 visibility = shadowTransmittance(origin, toLight, maxT, medium);
        if (dot(visibility, visibility) <= 0.0)
        {
            continue;
        }

        result += weight * arriving * visibility;
    }

    return result;
}

// --- The path --------------------------------------------------------------------------

// One light path, carried iteratively. `throughput` is the product of every scattering
// weight so far, which is what replaces the recursion the rendering equation implies and
// GLSL cannot express.
vec3 pathTrace(vec3 ro, vec3 rd, inout uint seed)
{
    vec3 radiance   = vec3(0.0);
    vec3 throughput = vec3(1.0);

    // The medium the ray is travelling through. One at a time, not a stack: a transmissive
    // solid nested inside another gives a wrong result, which documents/transparency.md names
    // rather than hides. Two *overlapping* solids are fine -- the interval union coalesces
    // them into a single pair of boundaries.
    //
    // Zero absorption and zero scattering is vacuum, so no companion flag is needed: every
    // expression below reduces to the vacuum case on its own.
    vec3  mediumAbsorption = vec3(0.0);   // sigma_a
    float mediumScatter    = 0.0;         // sigma_s
    float mediumG          = 0.0;

    for (int bounce = 0; bounce < uMaxBounces; ++bounce)
    {
        Hit hit = traceScene(ro, rd, false, INF);

        if (!hit.found)
        {
            // A ray that escapes brings back the environment, which is a light like any
            // other -- black by default, so nothing.
            radiance += throughput * BACKGROUND;
            break;
        }

        // --- Does the ray reach the surface at all? -----------------------------------
        //
        // Inside a scattering medium it may not. The distance to a scattering event is drawn
        // from sigma_s alone rather than from the full extinction, and absorption is then
        // carried analytically. That choice is what makes the two branches below share one
        // weight, exp(-sigma_a * distance travelled), and it is why a medium with no
        // scattering reproduces iteration 5 exactly rather than approximately: the sampling
        // never happens, and the surviving line is the one that was already there.
        bool  scattered = false;
        float flight    = 0.0;

#if CHROMA_MEDIA
        if (mediumScatter > 0.0)
        {
            flight    = -log(max(1.0 - rand(seed), TINY)) / mediumScatter;
            scattered = flight < hit.t;
        }
#endif

        // Absorption belongs to the segment just travelled, not to either end of it, so it is
        // applied here -- the first moment the segment's length is known. Doing it at the
        // point of entry instead would need to know where the ray leaves, which is the one
        // thing not yet computed. The two branches share this line rather than each writing
        // their own, because the weight really is the same: exp(-sigma_a * distance).
        float travelled = scattered ? flight : hit.t;
        throughput *= exp(-mediumAbsorption * travelled);

        if (dot(throughput, throughput) < TINY)
        {
            break;
        }

        vec3     point    = ro + travelled * rd;
        vec3     view     = -rd;
        Material material = fetchMaterial(hit.primitive);
        float    eta      = relativeIor(material, hit.entering);

        // At a scattering point the "axis" is the direction of travel rather than a normal,
        // and the surface fields above are inert. Both vertex kinds go through the SAME
        // directLight call below: two calls would be inlined separately, each carrying its
        // own copy of the tape walk, and the shader would stop linking. That is not a
        // hypothetical -- it is what the first version of this iteration did.
        vec3 axis;
        if (scattered)
        {
            axis = rd;
        }
        else
        {
            axis = hitNormal(hit, point);

            // Emissive surfaces are never sampled by the loop below -- a CSG solid has no
            // parameterisation to sample -- so being hit is the only way they are ever
            // counted, and there is no double counting to correct for. It is also the whole
            // reason a caustic is reachable at all: an emissive solid can be hit, a light
            // cannot.
            radiance += throughput * material.emission;
        }

        vec3 direct = directLight(
            point, axis, view, material, eta,
            !scattered, mediumG, mediumAbsorption + vec3(mediumScatter), seed) * throughput;

        // The directly visible lighting is left exact; only what arrives through a bounce
        // is capped, because that is where fireflies come from.
        radiance += bounce == 0 ? direct : min(direct, vec3(FIREFLY_CLAMP));

        if (scattered)
        {
            // Drawn from the phase function itself, so there is no weight to apply -- the
            // same cancellation the cosine-weighted hemisphere gets. A scattering event costs
            // a bounce like any other vertex, so dense fog can spend the whole budget without
            // reaching a surface: that is the fixed path length's bias made visible, not a
            // new one.
            rd = samplePhaseHg(rd, mediumG, rand2(seed));
            ro = point;
            continue;
        }

        vec3 next;
        vec3 weight;
        bool transmitted;
        if (!sampleBrdf(material, axis, view, eta, seed, next, weight, transmitted))
        {
            break;
        }

        throughput *= weight;

        if (dot(throughput, throughput) < TINY)
        {
            break;   // nothing left to gather; the remaining bounces are pure cost
        }

        if (transmitted)
        {
            // The ray crossed to the other side, so the offset crosses with it. Getting
            // this sign wrong makes the ray immediately re-hit the face it just went
            // through and the path dies there -- the symptom is glass that renders
            // perfectly black, which reads as an absorption bug and is not one.
            ro = point - axis * SHADOW_BIAS;

            // Leaving zeroes the medium rather than merely flagging it unused: every
            // expression that reads it treats vacuum as zero, so there is no state left to
            // get out of step with where the ray actually is.
            mediumAbsorption = hit.entering ? material.absorption : vec3(0.0);
            mediumScatter    = hit.entering ? material.scattering : 0.0;
            mediumG          = hit.entering ? material.anisotropy : 0.0;
        }
        else
        {
            ro = point + axis * SHADOW_BIAS;
        }

        rd = next;
    }

    return radiance;
}

// One sample for one pixel, folded into what that pixel already holds.
//
// Everything that is actually the renderer lives here, on both stages. What each stage's main
// does is find its pixel, read the history, and put the answer somewhere -- which is the whole
// difference between them.
vec4 traceSample(ivec2 pixel, vec2 centre, vec4 history)
{
    uint seed = seedFor(pixel, uSampleIndex);

    // Jitter inside the pixel. Frames accumulate anyway, so antialiasing is one line.
    vec2 ndc = centre + (rand2(seed) - 0.5) * 2.0 * uInvResolution;

    vec3 origin    = uCameraPosition;
    vec3 direction = normalize(uCameraForward + ndc.x * uCameraRight + ndc.y * uCameraUp);

    vec3 radiance = pathTrace(origin, direction, seed);

    // Running average, not a growing sum: a sum loses precision in a 32-bit float long
    // before a long render finishes.
    float weight = 1.0 / float(uSampleIndex + 1);

    // Alpha carries the running average of the squared luminance, which is what turns the
    // running mean into a variance: Var = E[L^2] - E[L]^2. convergence.frag reads it back to
    // estimate how much noise is left. The channel was unused -- the resolve pass only ever
    // reads .rgb -- so the metric costs no extra buffer and no extra pass over the scene.
    float lum = dot(radiance, vec3(0.2126, 0.7152, 0.0722));

    return vec4(mix(history.rgb, radiance, weight),
                mix(history.a, lum * lum, weight));
}

#if CHROMA_COMPUTE

// 8x8 rather than a row: a warp then covers a square of the image, and neighbouring rays in a
// path tracer go to neighbouring places. A 64-wide row would have the same occupancy and worse
// coherence at every bounce.
layout(local_size_x = 8, local_size_y = 8) in;

void main()
{
    ivec2 pixel = ivec2(gl_GlobalInvocationID.xy);
    ivec2 size  = imageSize(uAccumulation);

    // The dispatch is rounded up to whole workgroups, so the last one runs off the edge.
    if (pixel.x >= size.x || pixel.y >= size.y)
    {
        return;
    }

    // The centre of the pixel in NDC, which is what the vertex stage interpolates on the other
    // path. Reading and writing the same texel of the same image is defined: one invocation owns
    // one pixel, and no invocation reads a pixel another one writes.
    vec2 centre = ((vec2(pixel) + 0.5) / vec2(size)) * 2.0 - 1.0;

    imageStore(uAccumulation, pixel, traceSample(pixel, centre, imageLoad(uAccumulation, pixel)));
}

#else

void main()
{
    FragColor = traceSample(
        ivec2(gl_FragCoord.xy),
        vNdc,
        texture(uHistory, vNdc * 0.5 + 0.5));
}

#endif
