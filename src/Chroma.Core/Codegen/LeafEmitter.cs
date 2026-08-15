using System.Globalization;
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
/// the sphere sweep's round cone stay hand-written in raytrace.glsl, where they can be read
/// against documents/csg-raytracing.md; this only writes the wrapper that calls them with
/// constants instead of with values fetched from a buffer.
/// </para>
/// <para>
/// The four primitives defined by a <b>list</b> are the exception, and the reason for the whole
/// rewrite. Their crossing arrays are sized from the list, so the loop that fills one cannot be
/// a shared function — GLSL 3.30 makes the length part of the type. Those loops are transcribed
/// here from the shader they replace, with three changes and no others: the loop bounds are this
/// leaf's own point count, the points are a <c>const</c> array rather than a texture fetch, and
/// the crossings land in a shared global. The truncation guards are gone because there is
/// nothing left to truncate.
/// </para>
/// <para>
/// The <b>shared global</b> is not a stylistic choice. A leaf owns its scratch for the length of
/// one call and no two leaves are ever in flight at once, so one array of each kind is all a
/// scene can want — but a driver that inlines everything allocates per variable, so a scratch
/// array declared inside the leaf becomes a fresh array for every leaf in the scene. A chess set
/// exhausted the register file on those alone.
/// </para>
/// </remarks>
internal sealed class LeafEmitter(SpanLibrary spans)
{
    /// <summary>One shared body, the global it answers into, and what it costs to call.</summary>
    /// <param name="Cost">
    /// What the body weighs, kept because sharing it saves <i>source</i> and not instructions:
    /// the driver inlines every call, so a body written once and called sixteen times is sixteen
    /// copies in the assembly and has to be counted sixteen times.
    /// </param>
    private readonly record struct Profile(string Name, int Spans, int Cost)
    {
        public string List => $"g{Name}";
    }

    /// <summary>The shared bodies, keyed on the geometry that makes two leaves the same solid.</summary>
    private readonly Dictionary<string, Profile> _profiles = [];

    private readonly GlslWriter _bodies = new();

    private int _crossings;
    private int _breaks;
    private int _deltas;

    /// <summary>Declares the shared scratch and the sorts over it.</summary>
    /// <remarks>
    /// The arrays are sized to the hungriest leaf in the scene; the sorts over them are bounded
    /// by <c>count</c> rather than by the array length, and that is a deliberate refusal to let
    /// the driver unroll them. An insertion sort's inner loop is data-dependent, so unrolling it
    /// buys nothing, but the compiler cannot know that from a constant bound and emits N squared
    /// copies anyway. A sixteen-segment lathe's sort alone came to some three thousand assembly
    /// instructions that way, and a chess set's worth of them hit the program's instruction
    /// ceiling. Instructions are the resource this shader runs out of after registers.
    /// </remarks>
    public void WriteHelpers(GlslWriter w)
    {
        WriteProfileLists(w);

        if (_crossings == 0 && _breaks == 0 && _deltas == 0)
        {
            return;
        }

        w.Line("// --- Leaf scratch ----------------------------------------------------------------");
        w.Line("// One array per kind, sized to the hungriest leaf in the scene and shared by all of");
        w.Line("// them: a leaf holds its scratch for the length of one call and no two are ever in");
        w.Line("// flight at once. See the remarks on Codegen/LeafEmitter for why these are not locals.");
        w.Line();

        if (_crossings > 0)
        {
            w.Line($"float gCross[{_crossings}];");
        }

        if (_breaks > 0)
        {
            w.Line($"float gBreak[{_breaks}];");
        }

        if (_deltas > 0)
        {
            w.Line($"int   gDelta[{_deltas}];");
        }

        w.Line();

        if (_crossings > 0)
        {
            WriteFloatSort(w, "sortCross", "gCross");
        }


        if (_breaks > 0)
        {
            WriteFloatSort(w, "sortBreak", "gBreak");
        }

        if (_deltas > 0)
        {
            WriteEventSort(w);
        }
    }

    private static void WriteFloatSort(GlslWriter w, string name, string array)
    {
        w.Line("// Insertion sort. Nothing is merged here, deliberately: two surfaces meeting at a vertex");
        w.Line("// legitimately produce two crossings a hair apart, and collapsing those breaks the parity");
        w.Line("// they were meant to protect. Duplicates are prevented instead, by having each edge own");
        w.Line("// its starting vertex and not its ending one.");
        w.Open($"void {name}(int count)");
        w.Open("for (int i = 1; i < count; ++i)");
        w.Line($"float key = {array}[i];");
        w.Line("int   j   = i - 1;");
        w.Line();
        w.Open($"for (; j >= 0 && {array}[j] > key; --j)");
        w.Line($"{array}[j + 1] = {array}[j];");
        w.Close();
        w.Line();
        w.Line($"{array}[j + 1] = key;");
        w.Close();
        w.Close();
        w.Line();
    }

    private static void WriteEventSort(GlslWriter w)
    {
        w.Line("// The same sort carrying two arrays: a sweep event's sign has to travel with its");
        w.Line("// position or the depth count means nothing.");
        w.Open("void sortEvents(int count)");
        w.Open("for (int i = 1; i < count; ++i)");
        w.Line("float key   = gCross[i];");
        w.Line("int   delta = gDelta[i];");
        w.Line("int   j     = i - 1;");
        w.Line();
        w.Open("for (; j >= 0 && gCross[j] > key; --j)");
        w.Line("gCross[j + 1] = gCross[j];");
        w.Line("gDelta[j + 1] = gDelta[j];");
        w.Close();
        w.Line();
        w.Line("gCross[j + 1] = key;");
        w.Line("gDelta[j + 1] = delta;");
        w.Close();
        w.Close();
        w.Line();
    }

    /// <summary>The global each shared body answers into.</summary>
    private void WriteProfileLists(GlslWriter w)
    {
        if (_profiles.Count == 0)
        {
            return;
        }

        w.Line("// --- Shared leaf bodies ----------------------------------------------------------");
        w.Line("// One global per distinct solid, holding what its body found. Every leaf that IS that");
        w.Line("// solid calls the body and copies the answer into its own slot; only the matrix that");
        w.Line("// places it differs. See the remarks on Codegen/LeafEmitter.");
        w.Line();

        foreach (Profile profile in _profiles.Values)
        {
            w.Line($"{spans.Type(profile.Spans)} {profile.List};");
        }

        w.Line();
    }

    /// <summary>The shared bodies themselves, written after the globals they answer into.</summary>
    public void WriteBodies(GlslWriter w)
    {
        if (_profiles.Count == 0)
        {
            return;
        }

        Paste(w, _bodies);
    }

    private static void Paste(GlslWriter target, GlslWriter source)
    {
        foreach (string line in source.ToString().TrimEnd('\n').Split('\n'))
        {
            target.Line(line);
        }

        target.Line();
    }

    /// <summary>Opens <c>void leafN(...)</c> and transforms the ray into the primitive's space.</summary>
    /// <remarks>
    /// The matrix is the one thing that is never shared. Two pawns on two squares are the same
    /// solid at two places, and the place is exactly what this holds.
    /// </remarks>
    private static void Head(GlslWriter w, LeafPlan plan, string target)
    {
        w.Line($"// {plan.Comment} -> {target}");
        w.Open($"void leaf{plan.Index}(vec3 ro, vec3 rd)");
        w.Line($"const mat4 M = {GlslWriter.Mat4(plan.ToLocal)};");
        w.Line("vec3 lo = (M * vec4(ro, 1.0)).xyz;");
        w.Line();
        w.Line("// w = 0 marks a direction rather than a point. It is NOT renormalised: under a scaling");
        w.Line("// transform the non-unit length is precisely what keeps the resulting t on the same");
        w.Line("// scale as every other primitive's.");
        w.Line("vec3 ld = (M * vec4(rd, 0.0)).xyz;");
        w.Line();
    }

    /// <summary>Writes one leaf, and returns what reaching it will cost the program.</summary>
    /// <remarks>
    /// The cost is what <b>one call site</b> weighs after the driver has inlined everything it
    /// reaches, which for a shared profile means the body counts here as well even though it was
    /// written into the file once. See <see cref="Profile"/>.
    /// </remarks>
    public int Write(GlslWriter w, LeafPlan plan, SpanRef target)
    {
        spans.Type(target.Spans);
        int before = w.Cost;

        if (!IsShareable(plan.Kind))
        {
            Head(w, plan, target.Variable);
            w.Line($"{target.Variable}.count = 0;");
            Body(w, plan, target.Variable, plan.Index.ToString(CultureInfo.InvariantCulture));
            w.Close();
            w.Line();
            return w.Cost - before;
        }

        Profile profile = ProfileFor(plan);

        Head(w, plan, target.Variable);
        w.Line($"{profile.Name}(lo, ld, {plan.Index});");
        w.Line($"{target.Variable} = {profile.List};");
        w.Close();
        w.Line();

        return w.Cost - before + profile.Cost;
    }

    /// <summary>Whether two leaves of this kind are worth pointing at one shared body.</summary>
    /// <remarks>
    /// Only the four defined by a <b>list</b>. Their bodies are a loop over that list against a
    /// compile-time bound, which the driver unrolls, so one of them is worth a hundred lines of
    /// assembly and thirty-two of them are worth what a whole program is allowed to be. The other
    /// six are a single <c>PUSH</c> calling hand-written maths, and routing one of those through a
    /// call and a list copy would cost more than emitting it twice.
    /// </remarks>
    private static bool IsShareable(PrimitiveKind kind) =>
        kind is PrimitiveKind.Prism or PrimitiveKind.Lathe
            or PrimitiveKind.Blob or PrimitiveKind.SphereSweep;

    /// <summary>The shared body for this leaf's geometry, emitting it if it is the first to ask.</summary>
    private Profile ProfileFor(LeafPlan plan)
    {
        string key = KeyOf(plan);

        if (_profiles.TryGetValue(key, out Profile existing))
        {
            return existing;
        }

        var profile = new Profile($"profile{_profiles.Count}", plan.Spans, 0);
        int before = _bodies.Cost;

        _bodies.Line($"// {profile.List}: {plan.Comment[..plan.Comment.IndexOf('—')].Trim()}");
        _bodies.Open($"void {profile.Name}(vec3 lo, vec3 ld, int leaf)");
        _bodies.Line($"{profile.List}.count = 0;");
        Body(_bodies, plan, profile.List, "leaf");
        _bodies.Close();
        _bodies.Line();

        profile = profile with { Cost = _bodies.Cost - before };
        _profiles[key] = profile;

        return profile;
    }

    /// <summary>
    /// What makes two leaves the same solid: everything except where it stands.
    /// </summary>
    /// <remarks>
    /// The points are compared as the text they will be emitted as, not as floats, because that
    /// is the thing being deduplicated — two outlines that round-trip to the same GLSL literals
    /// produce the same body whatever their bits say.
    /// </remarks>
    private static string KeyOf(LeafPlan plan)
    {
        var key = new System.Text.StringBuilder();
        key.Append(plan.Kind).Append('|').Append(plan.Spans).Append('|');
        key.Append(GlslWriter.Float(plan.ParamA)).Append('|');

        foreach (Vector2 point in plan.Points)
        {
            key.Append(GlslWriter.Float(point.X)).Append(',').Append(GlslWriter.Float(point.Y)).Append(';');
        }

        key.Append('|');

        foreach (Vector4 ball in plan.Balls)
        {
            key.Append(GlslWriter.Vec4(ball.X, ball.Y, ball.Z, ball.W)).Append(';');
        }

        key.Append('|');

        foreach (float strength in plan.Strengths)
        {
            key.Append(GlslWriter.Float(strength)).Append(';');
        }

        return key.ToString();
    }

    private void Body(GlslWriter w, LeafPlan plan, string list, string leaf)
    {
        switch (plan.Kind)
        {
            case PrimitiveKind.Sphere:
                Convex(w, plan, list, leaf, "sphereSpan(lo, ld)");
                break;
            case PrimitiveKind.Box:
                Convex(w, plan, list, leaf, "boxSpan(lo, ld)");
                break;
            case PrimitiveKind.Cylinder:
                Convex(w, plan, list, leaf, "cylinderSpan(lo, ld)");
                break;
            case PrimitiveKind.Cone:
                Convex(w, plan, list, leaf, $"coneSpan(lo, ld, {GlslWriter.Float(plan.ParamA)})");
                break;
            case PrimitiveKind.Plane:
                Convex(w, plan, list, leaf, "planeSpan(lo, ld)");
                break;
            case PrimitiveKind.Torus:
                Torus(w, plan, list, leaf);
                break;
            case PrimitiveKind.Prism:
                Prism(w, plan, list, leaf);
                break;
            case PrimitiveKind.Lathe:
                Lathe(w, plan, list, leaf);
                break;
            case PrimitiveKind.Blob:
                Blob(w, plan, list, leaf);
                break;
            default:
                SphereSweep(w, plan, list, leaf);
                break;
        }
    }

    private static void Convex(GlslWriter w, LeafPlan plan, string list, string leaf, string call)
    {
        w.Line($"PUSH({list}, tagSpan({call}, {leaf}));");
    }

    private static void Torus(GlslWriter w, LeafPlan plan, string list, string leaf)
    {
        w.Line($"int found = torusRoots(lo, ld, {GlslWriter.Float(plan.ParamA)});");
        w.Line();
        w.Unrolled("for (int k = 0; k + 1 < 4; k += 2)", 2);
        w.Line("if (k + 1 >= found) break;");
        w.Line($"PUSH({list}, tagSpan(spanOf(gRoots[k], gRoots[k + 1]), {leaf}));");
        w.Close();
    }

    /// <summary>
    /// A closed contour in XZ, swept from <c>y = 0</c> to <c>y = 1</c> and capped. Each edge
    /// extrudes into a planar wall the ray crosses at most once, so the crossings are a 2D
    /// problem; the caps are the slab, and clipping the paired spans to it is what a cap does.
    /// </summary>
    private void Prism(GlslWriter w, LeafPlan plan, string list, string leaf)
    {
        int edges = plan.Points.Count;
        _crossings = Math.Max(_crossings, edges);


        w.Line("float slabIn;");
        w.Line("float slabOut;");
        w.Line("if (!slabY(lo, ld, slabIn, slabOut)) return;");
        w.Line();
        Edges(w, plan);
        w.Line("int count = 0;");
        w.Line();
        w.Unrolled($"for (int e = 0; e < {edges}; ++e)", edges);
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
        w.Line("gCross[count] = u;");
        w.Line("count++;");
        w.Close();
        w.Line();
        w.Line("sortCross(count);");
        w.Line();
        w.Open("for (int k = 0; k + 1 < count; k += 2)");
        w.Line(
            $"PUSH({list}, tagSpan("
            + "spanOf(max(gCross[k], slabIn), min(gCross[k + 1], slabOut)), "
            + $"{leaf}));");
        w.Close();
    }

    /// <summary>
    /// A closed outline in the <c>(radius, y)</c> half-plane, revolved about Y. Each segment
    /// revolves into a cone frustum, and writing the segment parameter in terms of <c>y</c>
    /// makes the frustum's radius linear in <c>t</c> — so each segment is one quadratic.
    /// </summary>
    /// <remarks>
    /// The crossing bound is <b>twice</b> the segment count, which is the bound the shared
    /// 32-slot array never was: every band can be entered and exited by one ray. Sizing it from
    /// the segment count is what silently truncated a 24-segment lathe into a solid with a slice
    /// missing.
    /// </remarks>
    private void Lathe(GlslWriter w, LeafPlan plan, string list, string leaf)
    {
        int segments = plan.Points.Count;
        int crossings = 2 * segments;
        _crossings = Math.Max(_crossings, crossings);


        Edges(w, plan);
        w.Line("int count = 0;");
        w.Line();
        w.Unrolled($"for (int e = 0; e < {segments}; ++e)", segments);
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
        w.Line("gCross[count] = t;");
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
        w.Unrolled("for (int k = 0; k < 2; ++k)", 2);
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
        w.Line("gCross[count] = t;");
        w.Line("count++;");
        w.Close();
        w.Close();
        w.Line();
        w.Line("sortCross(count);");
        w.Line();
        w.Line("// A closed surface is crossed an even number of times, so an odd count means a tangency");
        w.Line("// was counted once instead of twice; the unpaired last crossing is dropped rather than");
        w.Line("// left to open a span that never closes.");
        w.Open("for (int k = 0; k + 1 < count; k += 2)");
        w.Line($"PUSH({list}, tagSpan(spanOf(gCross[k], gCross[k + 1]), {leaf}));");
        w.Close();
    }

    /// <summary>
    /// A sum of spherical fields. Each component contributes a quartic in <c>t</c>, and a sum of
    /// quartics is still one quartic — so between two consecutive component boundaries, where
    /// the live set does not change, the surface is a root of a single quartic.
    /// </summary>
    private void Blob(GlslWriter w, LeafPlan plan, string list, string leaf)
    {
        int components = plan.Balls.Count;
        int events = 2 * components;
        _crossings = Math.Max(_crossings, events);
        _breaks = Math.Max(_breaks, events);



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
        w.Line("int breakCount = 0;");
        w.Line();
        w.Unrolled($"for (int i = 0; i < {components}; ++i)", components);
        w.Line("vec3  d    = lo - balls[i].xyz;");
        w.Line("float b    = dot(d, ld);");
        w.Line("float c    = dot(d, d) - balls[i].w * balls[i].w;");
        w.Line("float disc = b * b - a * c;");
        w.Line();
        w.Line("if (disc < 0.0) continue;");
        w.Line();
        w.Line("float s = sqrt(disc);");
        w.Line("gBreak[breakCount] = (-b - s) / a; breakCount++;");
        w.Line("gBreak[breakCount] = (-b + s) / a; breakCount++;");
        w.Close();
        w.Line();
        w.Line("if (breakCount < 2) return;");
        w.Line();
        w.Line("sortBreak(breakCount);");
        w.Line();
        w.Line("int count = 0;");
        w.Line();
        w.Open($"for (int k = 0; k + 1 < breakCount && count < {events}; ++k)");
        w.Line();
        w.Line("float lo_ = gBreak[k];");
        w.Line("float hi_ = gBreak[k + 1];");
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
        w.Unrolled($"for (int i = 0; i < {components}; ++i)", components);
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
        w.Line(
            "int   found = solveQuartic(q3 * inv, q2 * inv, q1 * inv, "
            + $"(q0 - {GlslWriter.Float(plan.ParamA)}) * inv);");
        w.Line();
        w.Open($"for (int j = 0; j < found && count < {events}; ++j)");
        w.Line();
        w.Line("float t = gRoots[j] + mid;");
        w.Line();
        w.Line("// A root outside this stretch belongs to a polynomial that is not in force there.");
        w.Line("// The neighbouring interval will find it with the right coefficients.");
        w.Line("if (t <= lo_ || t >= hi_) continue;");
        w.Line();
        w.Line("gCross[count] = t;");
        w.Line("count++;");
        w.Close();
        w.Close();
        w.Line();
        w.Line("sortCross(count);");
        w.Line();
        w.Line("// The field is zero outside every component, so the ray always starts outside and");
        w.Line("// consecutive crossings pair without a parity flag.");
        w.Open("for (int k = 0; k + 1 < count; k += 2)");
        w.Line($"PUSH({list}, tagSpan(spanOf(gCross[k], gCross[k + 1]), {leaf}));");
        w.Close();
    }

    /// <summary>
    /// The union of one round cone per consecutive pair of spheres, done with a depth counter
    /// rather than by pairing crossings: consecutive hulls overlap by a whole sphere, and
    /// pairing would take a crossing buried inside the next segment for a surface.
    /// </summary>
    private void SphereSweep(GlslWriter w, LeafPlan plan, string list, string leaf)
    {
        int spheres = plan.Balls.Count;
        int events = 2 * (spheres - 1);
        _crossings = Math.Max(_crossings, events);
        _deltas = Math.Max(_deltas, events);


        w.Line($"const vec4 path[{spheres}] = vec4[{spheres}](");
        for (int i = 0; i < spheres; i++)
        {
            Vector4 ball = plan.Balls[i];
            string comma = i == spheres - 1 ? "" : ",";
            w.Line($"    {GlslWriter.Vec4(ball.X, ball.Y, ball.Z, ball.W)}{comma}");
        }

        w.Line(");");
        w.Line();
        w.Line("int count = 0;");
        w.Line();
        w.Unrolled($"for (int i = 0; i + 1 < {spheres}; ++i)", spheres - 1);
        w.Line("Span seg = roundConeSpan(lo, ld, path[i].xyz, path[i].w, path[i + 1].xyz, path[i + 1].w);");
        w.Line("if (seg.tOut - seg.tIn < EPS) continue;");
        w.Line();
        w.Line("gCross[count] = seg.tIn;  gDelta[count] =  1; count++;");
        w.Line("gCross[count] = seg.tOut; gDelta[count] = -1; count++;");
        w.Close();
        w.Line();
        w.Line("sortEvents(count);");
        w.Line();
        w.Line("int   depth = 0;");
        w.Line("float open  = 0.0;");
        w.Line();
        w.Open("for (int i = 0; i < count; ++i)");
        w.Line();
        w.Line("int before = depth;");
        w.Line("depth += gDelta[i];");
        w.Line();
        w.Line("if (before == 0 && depth > 0) { open = gCross[i]; }");
        w.Open("else if (before > 0 && depth == 0)");
        w.Line($"PUSH({list}, tagSpan(spanOf(open, gCross[i]), {leaf}));");
        w.Close();
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
