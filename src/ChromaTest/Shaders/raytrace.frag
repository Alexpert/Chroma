// The ray tracer. One primary ray per pixel, intersected against the scene tape.
//
// The whole scene arrives through three texture buffers, so this shader never changes when
// the scene does -- see documents/csg-raytracing.md for the encoding and the algorithm.
//
// A primitive does not answer "where is your nearest surface"; it returns every stretch of
// the ray that lies inside it, and the boolean operators merge those stretches. That is what
// makes a spherical cavity in a box come out with its normals pointing the right way.
#version 330 core

// --- Limits and shared constants -------------------------------------------------------
// PRIMITIVE_TEXELS, MATERIAL_TEXELS, MAX_SPANS and MAX_STACK mirror GpuLayout on the C#
// side. Nothing checks that the two agree, so they change together or not at all: the CPU
// rejects a scene that would overflow these, and it can only do that if it knows them.
const int PRIMITIVE_TEXELS = 5;   // (kind, materialIndex, 0, 0) + 4 matrix rows
const int MATERIAL_TEXELS  = 2;   // (r, g, b, specular) + (shininess, reflectivity, 0, 0)

const int MAX_SPANS = 8;   // spans in one list
const int MAX_STACK = 4;   // span lists held at once

const int MAX_LIGHTS = 8;

const int KIND_SPHERE   = 0;
const int KIND_BOX      = 1;
const int KIND_CYLINDER = 2;

const int OP_LEAF         = 0;
const int OP_UNION        = 1;
const int OP_INTERSECTION = 2;
const int OP_DIFFERENCE   = 3;
const int OP_END_ROOT     = 4;   // pops one list and folds it into the answer

// EPS is the geometric tolerance, in world units. TINY only guards divisions: a local ray
// direction can be legitimately small under a large scale, so reusing EPS there would
// reject perfectly good geometry.
const float EPS  = 1e-4;
const float TINY = 1e-12;
const float INF  = 1e30;

// Larger than EPS on purpose: the hit point carries rounding proportional to t, so a bias
// sized for t = 0 stipples the far side of a large scene.
const float SHADOW_BIAS = 1e-3;

const vec3 BACKGROUND = vec3(0.08, 0.09, 0.11);
const vec3 AMBIENT    = vec3(0.06, 0.065, 0.08);

// --- Inputs ----------------------------------------------------------------------------

in vec2 vNdc;
out vec4 FragColor;

uniform samplerBuffer  uPrimitives;
uniform isamplerBuffer uTape;
uniform samplerBuffer  uMaterials;
uniform int            uTapeLength;

// Right and Up already carry the field of view and the aspect ratio, so building a ray is
// one add and one normalise.
uniform vec3 uCameraPosition;
uniform vec3 uCameraForward;
uniform vec3 uCameraRight;
uniform vec3 uCameraUp;

uniform int  uLightCount;
uniform int  uLightKind[MAX_LIGHTS];    // 0 point, 1 directional
uniform vec3 uLightVector[MAX_LIGHTS];  // position, or the direction the light travels
uniform vec3 uLightColor[MAX_LIGHTS];   // colour already scaled by intensity

// --- Spans -----------------------------------------------------------------------------

// The stretch of a ray that lies inside a solid, with the surface crossed at each end.
//
// The surface is a reference, not a normal: carrying position and normal at both ends of
// every span, times MAX_SPANS, times MAX_STACK, is far more register pressure than a
// fragment shader can afford. The normal is recomputed once, at the end, from the one
// surface that turned out to be visible.
//
//   surf =  0            no surface (the +/-infinity ends of a complement)
//   surf = +(prim + 1)   the primitive's outward normal
//   surf = -(prim + 1)   that normal, negated -- the surface came from a subtracted operand
struct Span
{
    float tIn;
    float tOut;
    int   surfIn;
    int   surfOut;
};

// A solid, for one ray: spans sorted by tIn, disjoint and non-touching. That invariant is
// what the operators consume and what they must restore.
struct SpanList
{
    int  count;
    Span items[MAX_SPANS];
};

// tIn > tOut, so the width test below rejects it without a separate flag.
Span noSpan() { return Span(1.0, -1.0, 0, 0); }

// A ray grazing a sphere tangentially, or hitting a box exactly on an edge, gives
// tIn == tOut. Keeping those would leave zero-width slivers scattered along silhouettes.
//
// The count test is a backstop only: the CPU rejects any scene whose worst case exceeds
// MAX_SPANS, precisely so that nothing is ever silently dropped here.
void push(inout SpanList list, Span span)
{
    if (span.tOut - span.tIn < EPS) return;
    if (list.count >= MAX_SPANS)    return;

    list.items[list.count] = span;
    list.count++;
}

// --- The three operators ---------------------------------------------------------------

// Sorted merge with coalescing. Interior surfaces vanish, which is correct: they are no
// longer on the boundary of the result.
void csgUnion(SpanList a, SpanList b, out SpanList result)
{
    result.count = 0;

    int  i = 0;
    int  j = 0;
    bool open = false;
    Span current = noSpan();

    // Bounded rather than while(true): every iteration consumes exactly one input span.
    for (int step = 0; step < 2 * MAX_SPANS; ++step)
    {
        if (i >= a.count && j >= b.count) break;

        Span next;
        if (j >= b.count || (i < a.count && a.items[i].tIn <= b.items[j].tIn))
        {
            next = a.items[i];
            i++;
        }
        else
        {
            next = b.items[j];
            j++;
        }

        if (!open)
        {
            current = next;
            open = true;
        }
        else if (next.tIn <= current.tOut + EPS)
        {
            // Touching counts as overlapping: leaving a hairline gap would break the
            // "non-touching" invariant that csgComplement depends on.
            if (next.tOut > current.tOut)
            {
                current.tOut    = next.tOut;
                current.surfOut = next.surfOut;
            }
        }
        else
        {
            push(result, current);
            current = next;
        }
    }

    if (open) push(result, current);
}

// Two-pointer sweep. Each emitted span takes its entry from whichever operand entered last
// and its exit from whichever leaves first -- those are the surfaces actually bounding the
// result.
void csgIntersection(SpanList a, SpanList b, out SpanList result)
{
    result.count = 0;

    int i = 0;
    int j = 0;

    for (int step = 0; step < 2 * MAX_SPANS; ++step)
    {
        if (i >= a.count || j >= b.count) break;

        Span x = a.items[i];
        Span y = b.items[j];
        Span s;

        if (x.tIn > y.tIn) { s.tIn = x.tIn; s.surfIn = x.surfIn; }
        else               { s.tIn = y.tIn; s.surfIn = y.surfIn; }

        if (x.tOut < y.tOut) { s.tOut = x.tOut; s.surfOut = x.surfOut; }
        else                 { s.tOut = y.tOut; s.surfOut = y.surfOut; }

        push(result, s);

        // Advance past whichever ends first; the other may still meet the next one.
        if (x.tOut < y.tOut) i++; else j++;
    }
}

// The gaps between the spans, extended to +/-infinity, with every surface flipped.
//
// The flip is the whole point. Where a surface of the subtracted solid bounds the result,
// the ray is leaving that solid's interior, so its outward normal points *into* what
// remains. Negating it is what makes the inside of a drilled hole shade instead of going
// black -- and it is the single most commonly botched detail in a CSG renderer.
void csgComplement(SpanList a, out SpanList result)
{
    result.count = 0;

    float cursor = -INF;
    int   surf   = 0;      // the -infinity end bounds nothing

    for (int i = 0; i < MAX_SPANS; ++i)
    {
        if (i >= a.count) break;

        Span gap;
        gap.tIn     = cursor;
        gap.surfIn  = surf;
        gap.tOut    = a.items[i].tIn;
        gap.surfOut = -a.items[i].surfIn;
        push(result, gap);

        cursor = a.items[i].tOut;
        surf   = -a.items[i].surfOut;
    }

    push(result, Span(cursor, INF, surf, 0));
}

// A \ B == A n complement(B). One small complement plus the intersection that already
// exists, instead of a third merge loop with its own way of being subtly wrong.
//
// complement(B) holds one more span than B. It always fits: the CPU sizes MAX_SPANS against
// |A| + |B|, and every subtree produces at least one span, so |B| + 1 <= |A| + |B|.
void csgDifference(SpanList a, SpanList b, out SpanList result)
{
    SpanList complement;
    csgComplement(b, complement);
    csgIntersection(a, complement, result);
}

// --- Primitives ------------------------------------------------------------------------
// Every primitive is evaluated in its own canonical space, so the shader reads no shape
// parameters at all -- the dimensions live in the matrix.

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
    return Span((-b - s) / a, (-b + s) / a, 0, 0);
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

    return tIn > tOut ? noSpan() : Span(tIn, tOut, 0, 0);
}

// Canonical cylinder: radius 1 about +Y, from y = 0 to y = 1. It is the intersection of an
// infinite tube with a slab, and both are spans, so no special case is needed for the caps.
Span cylinderSpan(vec3 ro, vec3 rd)
{
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

    float slabIn  = -INF;
    float slabOut =  INF;

    if (abs(rd.y) < TINY)
    {
        if (ro.y < 0.0 || ro.y > 1.0)
        {
            return noSpan();
        }
    }
    else
    {
        float ta = (0.0 - ro.y) / rd.y;
        float tb = (1.0 - ro.y) / rd.y;
        slabIn  = min(ta, tb);
        slabOut = max(ta, tb);
    }

    float tIn  = max(tubeIn, slabIn);
    float tOut = min(tubeOut, slabOut);

    return tIn > tOut ? noSpan() : Span(tIn, tOut, 0, 0);
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
        texelFetch(uPrimitives, base + 1),
        texelFetch(uPrimitives, base + 2),
        texelFetch(uPrimitives, base + 3),
        texelFetch(uPrimitives, base + 4));
}

vec3 primitiveNormal(int kind, vec3 p)
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

    if (p.y < EPS)       return vec3(0.0, -1.0, 0.0);
    if (p.y > 1.0 - EPS) return vec3(0.0,  1.0, 0.0);
    return normalize(vec3(p.x, 0.0, p.z));
}

Span primitiveSpan(int kind, vec3 ro, vec3 rd)
{
    if (kind == KIND_SPHERE) return sphereSpan(ro, rd);
    if (kind == KIND_BOX)    return boxSpan(ro, rd);
    return cylinderSpan(ro, rd);
}

// One leaf's span list: at most one span, since all three primitives are convex.
void leafSpans(int primitive, vec3 ro, vec3 rd, out SpanList list)
{
    list.count = 0;

    int  base    = primitive * PRIMITIVE_TEXELS;
    int  kind    = int(texelFetch(uPrimitives, base).x);
    mat4 toLocal = fetchMatrix(base);

    vec3 lo = (toLocal * vec4(ro, 1.0)).xyz;

    // w = 0 marks a direction rather than a point. It is NOT renormalised: under a scaling
    // transform the non-unit length is precisely what keeps the resulting t on the same
    // scale as every other primitive's.
    vec3 ld = (toLocal * vec4(rd, 0.0)).xyz;

    Span span = primitiveSpan(kind, lo, ld);
    span.surfIn  = primitive + 1;
    span.surfOut = primitive + 1;

    push(list, span);
}

// --- Tracing ---------------------------------------------------------------------------

struct Hit
{
    bool  found;
    float t;
    int   primitive;
    bool  flip;
};

Hit noHit()
{
    Hit h;
    h.found     = false;
    h.t         = INF;
    h.primitive = 0;
    h.flip      = false;
    return h;
}

// The visible surface of one finished root, folded into the running best.
void resolveRoot(SpanList list, inout Hit best)
{
    for (int i = 0; i < MAX_SPANS; ++i)
    {
        if (i >= list.count) break;

        Span span = list.items[i];
        if (span.tOut < EPS) continue;   // entirely behind the eye

        // Spans are sorted, so the first one still ahead is the visible one for this root;
        // whatever it decides, this root has had its say.
        float t;
        int   surf;
        bool  inside;

        if (span.tIn > EPS)
        {
            t = span.tIn;  surf = span.surfIn;  inside = false;
        }
        else
        {
            // The ray started inside: the visible surface is where it leaves, seen from
            // behind, so the normal is reversed on top of whatever the encoding says.
            t = span.tOut; surf = span.surfOut; inside = true;
        }

        // surf == 0 is an unbounded end that survived a complement. None of the three
        // primitives is unbounded, so this cannot happen today; it costs one compare to
        // shade nothing rather than to shade primitive -1.
        if (surf != 0 && t < best.t)
        {
            best.found     = true;
            best.t         = t;
            best.primitive = abs(surf) - 1;
            best.flip      = (surf < 0) != inside;
        }

        return;
    }
}

bool rootOccludes(SpanList list, float maxT)
{
    for (int i = 0; i < MAX_SPANS; ++i)
    {
        if (i >= list.count) break;

        if (list.items[i].tOut > EPS && list.items[i].tIn < maxT - EPS)
        {
            return true;
        }
    }

    return false;
}

// The stack machine. GLSL has no recursion, so the CPU hands over the tree in post-order
// and this walks it with an explicit stack of span lists.
//
// anyHit answers a different question -- "is anything in the way at all" -- and returns as
// soon as it knows. It deliberately does NOT apply the "started inside" rule: a surface
// must not shadow itself.
Hit runTape(vec3 ro, vec3 rd, bool anyHit, float maxT)
{
    SpanList stack[MAX_STACK];
    SpanList merged;
    int sp = 0;

    Hit best = noHit();

    for (int i = 0; i < uTapeLength; ++i)
    {
        ivec4 instruction = texelFetch(uTape, i);
        int   opcode      = instruction.x;

        if (opcode == OP_LEAF)
        {
            leafSpans(instruction.y, ro, rd, stack[sp]);
            sp++;
        }
        else if (opcode == OP_END_ROOT)
        {
            // Roots are implicitly unioned, but they are resolved one at a time rather
            // than merged: the span budget then applies per root, so a scene may hold any
            // number of separate solids however tight MAX_SPANS is.
            sp--;

            if (anyHit)
            {
                if (rootOccludes(stack[sp], maxT))
                {
                    best.found = true;
                    return best;
                }
            }
            else
            {
                resolveRoot(stack[sp], best);
            }
        }
        else
        {
            if (opcode == OP_UNION)             csgUnion(stack[sp - 2], stack[sp - 1], merged);
            else if (opcode == OP_INTERSECTION) csgIntersection(stack[sp - 2], stack[sp - 1], merged);
            else                                csgDifference(stack[sp - 2], stack[sp - 1], merged);

            sp -= 2;
            stack[sp] = merged;
            sp++;
        }
    }

    return best;
}

Hit trace(vec3 ro, vec3 rd)
{
    return runTape(ro, rd, false, INF);
}

bool occluded(vec3 ro, vec3 rd, float maxT)
{
    return runTape(ro, rd, true, maxT).found;
}

vec3 hitNormal(Hit hit, vec3 point)
{
    int  base    = hit.primitive * PRIMITIVE_TEXELS;
    int  kind    = int(texelFetch(uPrimitives, base).x);
    mat4 toLocal = fetchMatrix(base);

    vec3 local  = (toLocal * vec4(point, 1.0)).xyz;
    vec3 normal = primitiveNormal(kind, local);

    // Normals transform by the inverse transpose. toLocal already IS the inverse, so its
    // transpose is the normal matrix. Using mat3(toLocal) instead agrees for pure
    // rotations, which is why that mistake survives every test scene without a scale.
    normal = normalize(transpose(mat3(toLocal)) * normal);

    return hit.flip ? -normal : normal;
}

// --- Shading ---------------------------------------------------------------------------

vec3 shade(Hit hit, vec3 point, vec3 normal, vec3 viewDir)
{
    int  materialIndex = int(texelFetch(uPrimitives, hit.primitive * PRIMITIVE_TEXELS).y);
    vec4 first  = texelFetch(uMaterials, materialIndex * MATERIAL_TEXELS);
    vec4 second = texelFetch(uMaterials, materialIndex * MATERIAL_TEXELS + 1);

    vec3  albedo    = first.rgb;
    float specular  = first.a;
    float shininess = second.x;

    vec3 result = AMBIENT * albedo;

    // Offset along the normal, not along the ray: inside a cavity the normal points into
    // the hollow, which is exactly the side the shadow ray has to start on.
    vec3 shadowOrigin = point + normal * SHADOW_BIAS;

    for (int i = 0; i < uLightCount; ++i)
    {
        bool  isPoint = uLightKind[i] == 0;
        vec3  toLight = isPoint
            ? normalize(uLightVector[i] - point)
            : -uLightVector[i];              // stored as the direction the light travels

        float lambert = max(dot(normal, toLight), 0.0);
        if (lambert <= 0.0)
        {
            continue;
        }

        float maxT = isPoint ? length(uLightVector[i] - point) : INF;
        if (occluded(shadowOrigin, toLight, maxT))
        {
            continue;
        }

        result += albedo * uLightColor[i] * lambert;

        if (specular > 0.0)
        {
            vec3 halfway = normalize(toLight + viewDir);
            result += uLightColor[i] * specular * pow(max(dot(normal, halfway), 0.0), shininess);
        }
    }

    return result;
}

void main()
{
    vec3 origin    = uCameraPosition;
    vec3 direction = normalize(uCameraForward + vNdc.x * uCameraRight + vNdc.y * uCameraUp);

    Hit hit = trace(origin, direction);

    if (!hit.found)
    {
        FragColor = vec4(BACKGROUND, 1.0);
        return;
    }

    vec3 point  = origin + hit.t * direction;
    vec3 normal = hitNormal(hit, point);

    FragColor = vec4(shade(hit, point, normal, -direction), 1.0);
}
