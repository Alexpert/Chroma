// The ray tracer. One primary ray per pixel, intersected against the scene tape.
//
// The whole scene arrives through three texture buffers, so this shader never changes when
// the scene does -- see documents/csg-raytracing.md for the encoding and the algorithm.
//
// Iteration 2: the tape holds leaves only, and the "combination" is the nearest entry among
// them, which is the implicit union of the top-level solids. Spans are already the shape
// everything returns, so the CSG operators plug into this without reshaping it.
#version 330 core

// --- Limits and shared constants -------------------------------------------------------
// PRIMITIVE_TEXELS and MATERIAL_TEXELS mirror GpuLayout on the C# side. Nothing checks
// that the two agree, so they change together or not at all.
const int PRIMITIVE_TEXELS = 5;   // (kind, materialIndex, 0, 0) + 4 matrix rows
const int MATERIAL_TEXELS  = 2;   // (r, g, b, specular) + (shininess, reflectivity, 0, 0)

const int MAX_LIGHTS = 8;

const int KIND_SPHERE   = 0;
const int KIND_BOX      = 1;
const int KIND_CYLINDER = 2;

const int OP_LEAF = 0;

// EPS is the geometric tolerance, in world units. TINY only guards divisions: a local ray
// direction can be legitimately small under a large scale, so reusing EPS there would
// reject perfectly good geometry.
const float EPS  = 1e-4;
const float TINY = 1e-12;
const float INF  = 1e30;

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

// The stretch of a ray that lies inside a solid. Convex primitives produce at most one.
struct Span
{
    float tIn;
    float tOut;
};

// tIn > tOut, so the emptiness test below rejects it without a separate flag.
Span noSpan() { return Span(1.0, -1.0); }

bool isEmpty(Span s) { return s.tOut - s.tIn < EPS; }

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
    return Span((-b - s) / a, (-b + s) / a);
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

    return tIn > tOut ? noSpan() : Span(tIn, tOut);
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

    return tIn > tOut ? noSpan() : Span(tIn, tOut);
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

// --- Tracing ---------------------------------------------------------------------------

struct Hit
{
    bool  found;
    float t;
    int   primitive;
    bool  flip;     // the ray started inside, so a back face is being viewed
};

Hit trace(vec3 ro, vec3 rd)
{
    Hit best;
    best.found     = false;
    best.t         = INF;
    best.primitive = 0;
    best.flip      = false;

    for (int i = 0; i < uTapeLength; ++i)
    {
        ivec4 instruction = texelFetch(uTape, i);
        if (instruction.x != OP_LEAF)
        {
            continue;   // operators are rejected on the CPU until the next iteration
        }

        int primitive = instruction.y;
        int base      = primitive * PRIMITIVE_TEXELS;
        int kind      = int(texelFetch(uPrimitives, base).x);

        mat4 toLocal = fetchMatrix(base);
        vec3 lo = (toLocal * vec4(ro, 1.0)).xyz;

        // w = 0 marks a direction rather than a point. It is NOT renormalised: under a
        // scaling transform the non-unit length is precisely what keeps the resulting t
        // on the same scale as every other primitive's.
        vec3 ld = (toLocal * vec4(rd, 0.0)).xyz;

        Span span = primitiveSpan(kind, lo, ld);

        // A grazing ray produces tIn == tOut. Keeping those would put zero-width slivers
        // on every silhouette.
        if (isEmpty(span) || span.tOut < EPS)
        {
            continue;
        }

        bool  inside = span.tIn <= EPS;
        float t      = inside ? span.tOut : span.tIn;

        if (t < best.t)
        {
            best.found     = true;
            best.t         = t;
            best.primitive = primitive;
            best.flip      = inside;
        }
    }

    return best;
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

    for (int i = 0; i < uLightCount; ++i)
    {
        vec3 toLight = uLightKind[i] == 0
            ? normalize(uLightVector[i] - point)
            : -uLightVector[i];              // stored as the direction the light travels

        float lambert = max(dot(normal, toLight), 0.0);
        if (lambert <= 0.0)
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
