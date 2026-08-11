using System.Numerics;
using Chroma.Core.Compilation;

namespace Chroma.Core.Codegen;

/// <summary>
/// Emits one GLSL function per leaf: transform the ray into the primitive's own space,
/// evaluate it, and tag the resulting spans with the leaf's index.
/// </summary>
/// <remarks>
/// <para>
/// The <b>maths</b> of a primitive is not here. Sphere, box, cylinder, cone, plane, torus and
/// the sphere sweep's round cone stay hand-written in raytrace.frag, where they can be read
/// against documents/csg-raytracing.md; this only writes the wrapper that calls them with
/// constants instead of with values fetched from a buffer.
/// </para>
/// <para>
/// The four primitives defined by a <b>list</b> are the exception, and the reason for the whole
/// rewrite. Their crossing arrays are sized from the list, so the loop that fills one cannot be
/// a shared function — GLSL 3.30 makes the length part of the type. Those loops are transcribed
/// here from the shader they replace, with two changes and no others: the array is sized to
/// this leaf's own point count, and the points are a <c>const</c> array rather than a texture
/// fetch. The truncation guards are gone because there is nothing left to truncate.
/// </para>
/// </remarks>
internal sealed class LeafEmitter(SpanLibrary spans)
{
    private readonly SortedSet<int> _floatSorts = [];
    private readonly SortedSet<int> _eventSorts = [];

    /// <summary>Emits the shared sort helpers, one per array size that asked for one.</summary>
    public void WriteHelpers(GlslWriter w)
    {
        foreach (int n in _floatSorts)
        {
            w.Line("// Insertion sort over one crossing array. Nothing is merged here, deliberately: two");
            w.Line("// surfaces meeting at a vertex legitimately produce two crossings a hair apart, and");
            w.Line("// collapsing those breaks the parity they were meant to protect. Duplicates are");
            w.Line("// prevented instead, by having each edge own its starting vertex and not its ending one.");
            w.Open($"void sortFloats_{n}(inout float values[{n}], int count)");
            w.Open($"for (int i = 1; i < {n}; ++i)");
            w.Line("if (i >= count) break;");
            w.Line();
            w.Line("float key = values[i];");
            w.Line("int   j   = i - 1;");
            w.Line();
            w.Open($"for (int k = 0; k < {n}; ++k)");
            w.Line("if (j < 0 || values[j] <= key) break;");
            w.Line("values[j + 1] = values[j];");
            w.Line("j--;");
            w.Close();
            w.Line();
            w.Line("values[j + 1] = key;");
            w.Close();
            w.Close();
            w.Line();
        }

        foreach (int n in _eventSorts)
        {
            w.Line("// The same sort carrying two arrays: a sweep event's sign has to travel with its");
            w.Line("// position or the depth count means nothing.");
            w.Open($"void sortEvents_{n}(inout float values[{n}], inout int deltas[{n}], int count)");
            w.Open($"for (int i = 1; i < {n}; ++i)");
            w.Line("if (i >= count) break;");
            w.Line();
            w.Line("float key   = values[i];");
            w.Line("int   delta = deltas[i];");
            w.Line("int   j     = i - 1;");
            w.Line();
            w.Open($"for (int k = 0; k < {n}; ++k)");
            w.Line("if (j < 0 || values[j] <= key) break;");
            w.Line("values[j + 1] = values[j];");
            w.Line("deltas[j + 1] = deltas[j];");
            w.Line("j--;");
            w.Close();
            w.Line();
            w.Line("values[j + 1] = key;");
            w.Line("deltas[j + 1] = delta;");
            w.Close();
            w.Close();
            w.Line();
        }
    }

    /// <summary>Opens <c>void leafN(...)</c> and transforms the ray into local space.</summary>
    private void Head(GlslWriter w, LeafPlan plan)
    {
        w.Line($"// {plan.Comment}");
        w.Open($"void leaf{plan.Index}(vec3 ro, vec3 rd, out {spans.Type(plan.Spans)} list)");
        w.Line($"const mat4 M = {GlslWriter.Mat4(plan.ToLocal)};");
        w.Line("vec3 lo = (M * vec4(ro, 1.0)).xyz;");
        w.Line();
        w.Line("// w = 0 marks a direction rather than a point. It is NOT renormalised: under a scaling");
        w.Line("// transform the non-unit length is precisely what keeps the resulting t on the same");
        w.Line("// scale as every other primitive's.");
        w.Line("vec3 ld = (M * vec4(rd, 0.0)).xyz;");
        w.Line();
        w.Line("list.count = 0;");
    }

    public void Write(GlslWriter w, LeafPlan plan)
    {
        Head(w, plan);

        switch (plan.Kind)
        {
            case PrimitiveKind.Sphere:
                Convex(w, plan, "sphereSpan(lo, ld)");
                break;
            case PrimitiveKind.Box:
                Convex(w, plan, "boxSpan(lo, ld)");
                break;
            case PrimitiveKind.Cylinder:
                Convex(w, plan, "cylinderSpan(lo, ld)");
                break;
            case PrimitiveKind.Cone:
                Convex(w, plan, $"coneSpan(lo, ld, {GlslWriter.Float(plan.ParamA)})");
                break;
            case PrimitiveKind.Plane:
                Convex(w, plan, "planeSpan(lo, ld)");
                break;
            case PrimitiveKind.Torus:
                Torus(w, plan);
                break;
            case PrimitiveKind.Prism:
                Prism(w, plan);
                break;
            case PrimitiveKind.Lathe:
                Lathe(w, plan);
                break;
            case PrimitiveKind.Blob:
                Blob(w, plan);
                break;
            default:
                SphereSweep(w, plan);
                break;
        }

        w.Close();
        w.Line();
    }

    private void Convex(GlslWriter w, LeafPlan plan, string call)
    {
        w.Line($"push_{plan.Spans}(list, tagSpan({call}, {plan.Index}));");
    }

    private void Torus(GlslWriter w, LeafPlan plan)
    {
        w.Line("float roots[4];");
        w.Line($"int   found = torusRoots(lo, ld, {GlslWriter.Float(plan.ParamA)}, roots);");
        w.Line();
        w.Open("for (int k = 0; k + 1 < 4; k += 2)");
        w.Line("if (k + 1 >= found) break;");
        w.Line($"push_{plan.Spans}(list, tagSpan(spanOf(roots[k], roots[k + 1]), {plan.Index}));");
        w.Close();
    }

    /// <summary>
    /// A closed contour in XZ, swept from <c>y = 0</c> to <c>y = 1</c> and capped. Each edge
    /// extrudes into a planar wall the ray crosses at most once, so the crossings are a 2D
    /// problem; the caps are the slab, and clipping the paired spans to it is what a cap does.
    /// </summary>
    private void Prism(GlslWriter w, LeafPlan plan)
    {
        int edges = plan.Points.Count;
        _floatSorts.Add(edges);

        w.Line("float slabIn;");
        w.Line("float slabOut;");
        w.Line("if (!slabY(lo, ld, slabIn, slabOut)) return;");
        w.Line();
        Edges(w, plan);
        w.Line($"float crossings[{edges}];");
        w.Line("int   count = 0;");
        w.Line();
        w.Open($"for (int e = 0; e < {edges}; ++e)");
        w.Line("vec2 a = edges[e].xy;");
        w.Line("vec2 s = edges[e].zw - a;");
        w.Line();
        w.Line("float denom = ld.x * s.y - ld.z * s.x;");
        w.Line("if (abs(denom) < TINY) continue;   // the ray runs along this wall");
        w.Line();
        w.Line("vec2  wv = a - lo.xz;");
        w.Line("float u = (wv.x * s.y - wv.y * s.x) / denom;    // distance along the ray");
        w.Line("float v = (wv.x * ld.z - wv.y * ld.x) / denom;  // position along the edge");
        w.Line();
        w.Line("// Half-open, so a ray passing exactly through a vertex is counted once rather than");
        w.Line("// twice. Counting it twice flips the parity and the solid comes out striped.");
        w.Line("if (v < 0.0 || v >= 1.0) continue;");
        w.Line();
        w.Line("crossings[count] = u;");
        w.Line("count++;");
        w.Close();
        w.Line();
        w.Line($"sortFloats_{edges}(crossings, count);");
        w.Line();
        w.Open($"for (int k = 0; k + 1 < {edges}; k += 2)");
        w.Line("if (k + 1 >= count) break;");
        w.Line(
            $"push_{plan.Spans}(list, tagSpan("
            + "spanOf(max(crossings[k], slabIn), min(crossings[k + 1], slabOut)), "
            + $"{plan.Index}));");
        w.Close();
    }

    /// <summary>
    /// A closed outline in the <c>(radius, y)</c> half-plane, revolved about Y. Each segment
    /// revolves into a cone frustum, and writing the segment parameter in terms of <c>y</c>
    /// makes the frustum's radius linear in <c>t</c> — so each segment is one quadratic.
    /// </summary>
    /// <remarks>
    /// The crossing array is <b>twice</b> the segment count, which is the bound the shared
    /// 32-slot array never was: every band can be entered and exited by one ray. Sizing it from
    /// the segment count is what silently truncated a 24-segment lathe into a solid with a slice
    /// missing.
    /// </remarks>
    private void Lathe(GlslWriter w, LeafPlan plan)
    {
        int segments = plan.Points.Count;
        int crossings = 2 * segments;
        _floatSorts.Add(crossings);

        Edges(w, plan);
        w.Line($"float crossings[{crossings}];");
        w.Line("int   count = 0;");
        w.Line();
        w.Open($"for (int e = 0; e < {segments}; ++e)");
        w.Line("float r0 = edges[e].x;");
        w.Line("float y0 = edges[e].y;");
        w.Line("float r1 = edges[e].z;");
        w.Line("float y1 = edges[e].w;");
        w.Line();
        w.Line("float dy = y1 - y0;");
        w.Line();
        w.Open("if (abs(dy) < TINY)");
        w.Line("// A horizontal segment revolves into a flat annulus, and a plane crossing is a");
        w.Line("// linear solve rather than a quadratic one.");
        w.Line("if (abs(ld.y) < TINY) continue;");
        w.Line();
        w.Line("float t    = (y0 - lo.y) / ld.y;");
        w.Line("vec2  q    = lo.xz + t * ld.xz;");
        w.Line("float rho2 = dot(q, q);");
        w.Line("float rlo  = min(r0, r1);");
        w.Line("float rhi  = max(r0, r1);");
        w.Line();
        w.Line("if (rho2 < rlo * rlo || rho2 > rhi * rhi) continue;");
        w.Line();
        w.Line("crossings[count] = t;");
        w.Line("count++;");
        w.Line("continue;");
        w.Close();
        w.Line();
        w.Line("float sA = (lo.y - y0) / dy;");
        w.Line("float sB = ld.y / dy;");
        w.Line("float dr = r1 - r0;");
        w.Line();
        w.Line("float R0 = r0 + dr * sA;");
        w.Line("float R1 = dr * sB;");
        w.Line();
        w.Line("float a = dot(ld.xz, ld.xz) - R1 * R1;");
        w.Line("float b = dot(lo.xz, ld.xz) - R0 * R1;");
        w.Line("float c = dot(lo.xz, lo.xz) - R0 * R0;");
        w.Line();
        w.Line("float t0    = 0.0;");
        w.Line("float t1    = 0.0;");
        w.Line("int   found = 0;");
        w.Line();
        w.Open("if (abs(a) < TINY)");
        w.Open("if (abs(b) >= TINY)");
        w.Line("t0 = -0.5 * c / b;");
        w.Line("found = 1;");
        w.Close();
        w.Close();
        w.Open("else");
        w.Line("float disc = b * b - a * c;");
        w.Open("if (disc >= 0.0)");
        w.Line("float s = sqrt(disc);");
        w.Line("t0 = (-b - s) / a;");
        w.Line("t1 = (-b + s) / a;");
        w.Line("found = 2;");
        w.Close();
        w.Close();
        w.Line();
        w.Open("for (int k = 0; k < 2; ++k)");
        w.Line("if (k >= found) break;");
        w.Line();
        w.Line("float t = k == 0 ? t0 : t1;");
        w.Line("float s = sA + sB * t;");
        w.Line();
        w.Line("// Half-open, so the vertex two segments share is counted once rather than twice.");
        w.Line("// Counting it twice flips the parity of everything past it, and the symptom is a");
        w.Line("// band of the solid you can see straight through.");
        w.Line("if (s < 0.0 || s >= 1.0) continue;     // beyond the ends of this segment");
        w.Line("if (r0 + dr * s < 0.0)   continue;     // the mirror cone, through the axis");
        w.Line();
        w.Line("crossings[count] = t;");
        w.Line("count++;");
        w.Close();
        w.Close();
        w.Line();
        w.Line($"sortFloats_{crossings}(crossings, count);");
        w.Line();
        w.Line("// A closed surface is crossed an even number of times, so an odd count means a tangency");
        w.Line("// was counted once instead of twice; the unpaired last crossing is dropped rather than");
        w.Line("// left to open a span that never closes.");
        w.Open($"for (int k = 0; k + 1 < {crossings}; k += 2)");
        w.Line("if (k + 1 >= count) break;");
        w.Line($"push_{plan.Spans}(list, tagSpan(spanOf(crossings[k], crossings[k + 1]), {plan.Index}));");
        w.Close();
    }

    /// <summary>
    /// A sum of spherical fields. Each component contributes a quartic in <c>t</c>, and a sum of
    /// quartics is still one quartic — so between two consecutive component boundaries, where
    /// the live set does not change, the surface is a root of a single quartic.
    /// </summary>
    private void Blob(GlslWriter w, LeafPlan plan)
    {
        int components = plan.Balls.Count;
        int events = 2 * components;
        _floatSorts.Add(events);

        w.Line($"const vec4 balls[{components}] = vec4[{components}](");
        for (int i = 0; i < components; i++)
        {
            Vector4 ball = plan.Balls[i];
            string comma = i == components - 1 ? "" : ",";
            w.Line($"    {GlslWriter.Vec4(ball.X, ball.Y, ball.Z, ball.W)}{comma}");
        }

        w.Line(");");
        w.Line($"const float strengths[{components}] = float[{components}](");
        for (int i = 0; i < components; i++)
        {
            string comma = i == components - 1 ? "" : ",";
            w.Line($"    {GlslWriter.Float(plan.Strengths[i])}{comma}");
        }

        w.Line(");");
        w.Line();
        w.Line("float a = dot(ld, ld);");
        w.Line("if (a < TINY) return;");
        w.Line();
        w.Line("// Where each component wakes up and falls asleep. These are the only places the summed");
        w.Line("// polynomial changes, so they are where it has to be re-derived.");
        w.Line($"float breaks[{events}];");
        w.Line("int   breakCount = 0;");
        w.Line();
        w.Open($"for (int i = 0; i < {components}; ++i)");
        w.Line("vec3  d    = lo - balls[i].xyz;");
        w.Line("float b    = dot(d, ld);");
        w.Line("float c    = dot(d, d) - balls[i].w * balls[i].w;");
        w.Line("float disc = b * b - a * c;");
        w.Line();
        w.Line("if (disc < 0.0) continue;");
        w.Line();
        w.Line("float s = sqrt(disc);");
        w.Line("breaks[breakCount] = (-b - s) / a; breakCount++;");
        w.Line("breaks[breakCount] = (-b + s) / a; breakCount++;");
        w.Close();
        w.Line();
        w.Line("if (breakCount < 2) return;");
        w.Line();
        w.Line($"sortFloats_{events}(breaks, breakCount);");
        w.Line();
        w.Line($"float crossings[{events}];");
        w.Line("int   count = 0;");
        w.Line();
        w.Open($"for (int k = 0; k + 1 < {events}; ++k)");
        w.Line($"if (k + 1 >= breakCount || count >= {events}) break;");
        w.Line();
        w.Line("float lo_ = breaks[k];");
        w.Line("float hi_ = breaks[k + 1];");
        w.Line("if (hi_ - lo_ < EPS) continue;");
        w.Line();
        w.Line("float mid = 0.5 * (lo_ + hi_);");
        w.Line();
        w.Line("// Re-origin at the middle of the stretch. The quartic's coefficients go as the fourth");
        w.Line("// power of the origin's distance, so a camera six units away builds them out of numbers");
        w.Line("// near 1000 whose roots lie within one unit of each other -- three digits of a 32-bit");
        w.Line("// float gone before the solver starts.");
        w.Line("vec3 o = lo + mid * ld;");
        w.Line();
        w.Line("float q4 = 0.0;");
        w.Line("float q3 = 0.0;");
        w.Line("float q2 = 0.0;");
        w.Line("float q1 = 0.0;");
        w.Line("float q0 = 0.0;");
        w.Line();
        w.Open($"for (int i = 0; i < {components}; ++i)");
        w.Line("vec3  d  = o - balls[i].xyz;");
        w.Line("float r2 = balls[i].w * balls[i].w;");
        w.Line("float c  = dot(d, d);");
        w.Line();
        w.Line("// Asleep over this stretch, and adding its formula anyway would extend its field beyond");
        w.Line("// its own radius -- which is what makes a blob's components local.");
        w.Line("if (c >= r2) continue;");
        w.Line();
        w.Line("float b = dot(d, ld);");
        w.Line();
        w.Line("float al = -a / r2;");
        w.Line("float be = -2.0 * b / r2;");
        w.Line("float ga = 1.0 - c / r2;");
        w.Line();
        w.Line("// (al t^2 + be t + ga)^2, scaled by the strength.");
        w.Line("q4 += strengths[i] * al * al;");
        w.Line("q3 += strengths[i] * 2.0 * al * be;");
        w.Line("q2 += strengths[i] * (be * be + 2.0 * al * ga);");
        w.Line("q1 += strengths[i] * 2.0 * be * ga;");
        w.Line("q0 += strengths[i] * ga * ga;");
        w.Close();
        w.Line();
        w.Line("// No live component, or strengths that cancel exactly.");
        w.Line("if (abs(q4) < TINY) continue;");
        w.Line();
        w.Line("float inv = 1.0 / q4;");
        w.Line("float roots[4];");
        w.Line(
            "int   found = solveQuartic(q3 * inv, q2 * inv, q1 * inv, "
            + $"(q0 - {GlslWriter.Float(plan.ParamA)}) * inv, roots);");
        w.Line();
        w.Open("for (int j = 0; j < 4; ++j)");
        w.Line($"if (j >= found || count >= {events}) break;");
        w.Line();
        w.Line("float t = roots[j] + mid;");
        w.Line();
        w.Line("// A root outside this stretch belongs to a polynomial that is not in force there.");
        w.Line("// The neighbouring interval will find it with the right coefficients.");
        w.Line("if (t <= lo_ || t >= hi_) continue;");
        w.Line();
        w.Line("crossings[count] = t;");
        w.Line("count++;");
        w.Close();
        w.Close();
        w.Line();
        w.Line($"sortFloats_{events}(crossings, count);");
        w.Line();
        w.Line("// The field is zero outside every component, so the ray always starts outside and");
        w.Line("// consecutive crossings pair without a parity flag.");
        w.Open($"for (int k = 0; k + 1 < {events}; k += 2)");
        w.Line("if (k + 1 >= count) break;");
        w.Line($"push_{plan.Spans}(list, tagSpan(spanOf(crossings[k], crossings[k + 1]), {plan.Index}));");
        w.Close();
    }

    /// <summary>
    /// The union of one round cone per consecutive pair of spheres, done with a depth counter
    /// rather than by pairing crossings: consecutive hulls overlap by a whole sphere, and
    /// pairing would take a crossing buried inside the next segment for a surface.
    /// </summary>
    private void SphereSweep(GlslWriter w, LeafPlan plan)
    {
        int spheres = plan.Balls.Count;
        int events = 2 * (spheres - 1);
        _eventSorts.Add(events);

        w.Line($"const vec4 path[{spheres}] = vec4[{spheres}](");
        for (int i = 0; i < spheres; i++)
        {
            Vector4 ball = plan.Balls[i];
            string comma = i == spheres - 1 ? "" : ",";
            w.Line($"    {GlslWriter.Vec4(ball.X, ball.Y, ball.Z, ball.W)}{comma}");
        }

        w.Line(");");
        w.Line();
        w.Line($"float events[{events}];");
        w.Line($"int   deltas[{events}];");
        w.Line("int   count = 0;");
        w.Line();
        w.Open($"for (int i = 0; i + 1 < {spheres}; ++i)");
        w.Line("Span seg = roundConeSpan(lo, ld, path[i].xyz, path[i].w, path[i + 1].xyz, path[i + 1].w);");
        w.Line("if (seg.tOut - seg.tIn < EPS) continue;");
        w.Line();
        w.Line("events[count] = seg.tIn;  deltas[count] =  1; count++;");
        w.Line("events[count] = seg.tOut; deltas[count] = -1; count++;");
        w.Close();
        w.Line();
        w.Line($"sortEvents_{events}(events, deltas, count);");
        w.Line();
        w.Line("int   depth = 0;");
        w.Line("float open  = 0.0;");
        w.Line();
        w.Open($"for (int i = 0; i < {events}; ++i)");
        w.Line("if (i >= count) break;");
        w.Line();
        w.Line("int before = depth;");
        w.Line("depth += deltas[i];");
        w.Line();
        w.Line("if (before == 0 && depth > 0)      open = events[i];");
        w.Line(
            "else if (before > 0 && depth == 0) "
            + $"push_{plan.Spans}(list, tagSpan(spanOf(open, events[i]), {plan.Index}));");
        w.Close();
    }

    /// <summary>The contour, one texel-shaped <c>vec4</c> per edge, wrapping the last back to the first.</summary>
    private static void Edges(GlslWriter w, LeafPlan plan)
    {
        int n = plan.Points.Count;

        w.Line($"const vec4 edges[{n}] = vec4[{n}](");

        for (int i = 0; i < n; i++)
        {
            Vector2 a = plan.Points[i];
            Vector2 b = plan.Points[(i + 1) % n];
            string comma = i == n - 1 ? "" : ",";
            w.Line($"    {GlslWriter.Vec4(a.X, a.Y, b.X, b.Y)}{comma}");
        }

        w.Line(");");
        w.Line();
    }
}
