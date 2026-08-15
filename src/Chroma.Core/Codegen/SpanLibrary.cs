namespace Chroma.Core.Codegen;

/// <summary>One span list: how wide it is, and the name of the global that holds it.</summary>
internal readonly record struct SpanRef(int Spans, string Variable);

/// <summary>
/// Emits the span-list types and the boolean operators, one instantiation per pair of pool
/// slots the scene actually combines.
/// </summary>
/// <remarks>
/// <para>
/// GLSL 3.30 makes an array's length part of its type and has no generics, so a span list that
/// holds two spans and one that holds twenty-four are different types and every function over
/// them has to exist twice. That was the argument for one global <c>MAX_SPANS</c> sized for the
/// worst scene anyone could write — and it is what made every scene pay for the worst one.
/// Generating the instantiations turns "no generics" from a design constraint into a few lines
/// of string building.
/// </para>
/// <para>
/// The operators take <b>no parameters</b>. They name the pool globals they read and write, so
/// an operator over a given triple of slots exists once however many nodes ask for it. An array
/// parameter would be far more readable and does not survive contact with a driver: every call
/// is inlined and every inlined array parameter becomes storage of its own, which is what a
/// chess set ran out of. Since a pool slot is reused down the scene, naming slots rather than
/// sizes also dedupes hard — every rook in a chess set shares one set of operators.
/// </para>
/// <para>
/// The algorithms are transcribed from the hand-written shader they replace, comments and all,
/// with <c>MAX_SPANS</c> replaced by the operand's own size. They are not a reimplementation:
/// the loop bounds and the names are the only things that differ.
/// </para>
/// </remarks>
internal sealed class SpanLibrary
{
    private readonly SortedSet<int> _sizes = [];
    private readonly List<(string Kind, SpanRef A, SpanRef B, SpanRef R)> _operators = [];
    private readonly List<SpanRef> _roots = [];
    private readonly HashSet<string> _emitted = [];

    /// <summary>What one call to each of these weighs, once the driver has inlined it.</summary>
    /// <remarks>
    /// <para>
    /// Measured by writing each body out once and reading <see cref="GlslWriter.Cost"/>, rather
    /// than written down as a number here. The bodies differ only in the names of the globals
    /// they read, so one measurement is the truth for every instantiation, and a measurement
    /// cannot fall out of step with the emitter the way a constant would.
    /// </para>
    /// <para>
    /// They are also, and surprisingly, <b>independent of span width</b>. Every loop in them is
    /// bounded by a list's <c>count</c>, which is a runtime field, so the driver compiles the body
    /// once instead of unrolling it: an operator over two twenty-four-span lists costs the same as
    /// one over two singles. This is why a wide CSG tree is cheaper than its span counts suggest,
    /// and it is worth knowing before optimising the wrong thing.
    /// </para>
    /// </remarks>
    private static readonly Dictionary<string, int> BodyCosts = MeasureBodies();

    /// <summary>A call to <c>union_*</c>, after inlining.</summary>
    public static int UnionCost => BodyCosts["union"];

    /// <summary>A call to <c>intersect_*</c>, after inlining.</summary>
    public static int IntersectionCost => BodyCosts["intersect"];

    /// <summary>A call to <c>complement_*</c>, after inlining.</summary>
    public static int ComplementCost => BodyCosts["complement"];

    /// <summary>A call to <c>resolve_*</c> and one to <c>occludes_*</c>, which a shape pays once each.</summary>
    public static int RootCost => BodyCosts["resolve"] + BodyCosts["occludes"];

    private static Dictionary<string, int> MeasureBodies()
    {
        // Any three refs will do: only the names they lend the generated text change with them.
        SpanRef a = new(1, "a");
        SpanRef b = new(1, "b");
        SpanRef r = new(1, "r");

        return new Dictionary<string, int>
        {
            ["union"] = Measure(w => WriteUnion(w, a, b, r)),
            ["intersect"] = Measure(w => WriteIntersection(w, a, b, r)),
            ["complement"] = Measure(w => WriteComplement(w, a, r)),
            ["resolve"] = Measure(w => WriteResolve(w, a)),
            ["occludes"] = Measure(w => WriteOccludes(w, a)),
        };
    }

    private static int Measure(Action<GlslWriter> write)
    {
        GlslWriter w = new();
        write(w);
        return w.Cost;
    }

    public string Type(int spans)
    {
        _sizes.Add(spans);
        return $"SpanList_{spans}";
    }

    public string Union(SpanRef a, SpanRef b, SpanRef r) => Operator("union", a, b, r);

    public string Intersection(SpanRef a, SpanRef b, SpanRef r) => Operator("intersect", a, b, r);

    /// <summary>
    /// The gaps between one list's spans. <c>A \ B</c> is <c>A ∩ complement(B)</c>, and the
    /// emitter writes it as exactly those two calls rather than as a third merge loop with its
    /// own way of being subtly wrong.
    /// </summary>
    public string Complement(SpanRef a, SpanRef r) => Operator("complement", a, default, r);

    /// <summary>Resolving one finished root, by the name of the slot it landed in.</summary>
    public string Resolve(SpanRef root)
    {
        Register(root);
        return $"resolve_{root.Variable}";
    }

    /// <summary>Occlusion-testing one finished root, by the name of the slot it landed in.</summary>
    public string Occludes(SpanRef root)
    {
        Register(root);
        return $"occludes_{root.Variable}";
    }

    private void Register(SpanRef root)
    {
        Type(root.Spans);

        if (_emitted.Add($"root:{root.Variable}"))
        {
            _roots.Add(root);
        }
    }

    private string Operator(string kind, SpanRef a, SpanRef b, SpanRef r)
    {
        Type(a.Spans);
        Type(r.Spans);

        string name = b.Variable is null
            ? $"{kind}_{a.Variable}_{r.Variable}"
            : $"{kind}_{a.Variable}_{b.Variable}_{r.Variable}";

        if (b.Variable is not null)
        {
            Type(b.Spans);
        }

        if (_emitted.Add(name))
        {
            _operators.Add((kind, a, b, r));
        }

        return name;
    }

    public void WriteTo(GlslWriter w)
    {
        w.Line("// --- Span lists ------------------------------------------------------------------");
        w.Line("// One type per size the scene uses. A leaf that produces a single span holds a single");
        w.Line("// span; nothing is sized for a scene other than this one.");
        w.Line();

        foreach (int n in _sizes)
        {
            w.Line($"struct SpanList_{n} {{ int count; Span items[{n}]; }};");
        }

        w.Line();
        w.Line("// A macro rather than a function: push is called from every leaf and every operator,");
        w.Line("// against a different list each time, and a list parameter would cost an array per call");
        w.Line("// site. The guards are the two that matter -- a ray grazing a sphere tangentially, or");
        w.Line("// hitting a box exactly on an edge, gives tIn == tOut, and keeping those would leave");
        w.Line("// zero-width slivers along every silhouette. The capacity test cannot fire: the list is");
        w.Line("// sized to its own node's worst case, so a dropped span would be an emitter bug.");
        w.Line(
            "#define PUSH(L, S) do { Span sp_ = (S); "
            + "if (sp_.tOut - sp_.tIn >= EPS && L.count < L.items.length()) "
            + "{ L.items[L.count] = sp_; L.count++; } } while (false)");
        w.Line();
    }

    /// <summary>
    /// The operators and root resolvers, written after the pool they name has been declared.
    /// </summary>
    public void WriteOperators(GlslWriter w)
    {
        w.Line("// --- Operators -------------------------------------------------------------------");
        w.Line();

        foreach ((string kind, SpanRef a, SpanRef b, SpanRef r) in _operators)
        {
            switch (kind)
            {
                case "union":
                    WriteUnion(w, a, b, r);
                    break;
                case "intersect":
                    WriteIntersection(w, a, b, r);
                    break;
                default:
                    WriteComplement(w, a, r);
                    break;
            }
        }

        foreach (SpanRef root in _roots)
        {
            WriteResolve(w, root);
            WriteOccludes(w, root);
        }
    }

    private static void WriteUnion(GlslWriter w, SpanRef a, SpanRef b, SpanRef r)
    {
        w.Line($"// {r.Variable} = {a.Variable} u {b.Variable}");
        w.Line("// Sorted merge with coalescing. Interior surfaces vanish, which is correct: they are");
        w.Line("// no longer on the boundary of the result.");
        w.Open($"void union_{a.Variable}_{b.Variable}_{r.Variable}()");
        w.Line($"{r.Variable}.count = 0;");
        w.Line();
        w.Line("int  i = 0;");
        w.Line("int  j = 0;");
        w.Line("bool open = false;");
        w.Line("Span current = noSpan();");
        w.Line();
        w.Line("// Bounded rather than while(true): every iteration consumes exactly one input span.");
        w.Open($"for (int step = 0; step < {a.Variable}.count + {b.Variable}.count; ++step)");
        w.Line("Span next;");
        w.Open(
            $"if (j >= {b.Variable}.count || (i < {a.Variable}.count "
            + $"&& {a.Variable}.items[i].tIn <= {b.Variable}.items[j].tIn))");
        w.Line($"next = {a.Variable}.items[i];");
        w.Line("i++;");
        w.Close();
        w.Open("else");
        w.Line($"next = {b.Variable}.items[j];");
        w.Line("j++;");
        w.Close();
        w.Line();
        w.Open("if (!open)");
        w.Line("current = next;");
        w.Line("open = true;");
        w.Close();
        w.Open("else if (next.tIn <= current.tOut + EPS)");
        w.Line("// Touching counts as overlapping: leaving a hairline gap would break the");
        w.Line("// \"non-touching\" invariant that the complement depends on.");
        w.Open("if (next.tOut > current.tOut)");
        w.Line("current.tOut = next.tOut;");
        w.Line("current.surf = packSurf(surfIn(current), surfOut(next));");
        w.Close();
        w.Close();
        w.Open("else");
        w.Line($"PUSH({r.Variable}, current);");
        w.Line("current = next;");
        w.Close();
        w.Close();
        w.Line();
        w.Open("if (open)");
        w.Line($"PUSH({r.Variable}, current);");
        w.Close();
        w.Close();
        w.Line();
    }

    private static void WriteIntersection(GlslWriter w, SpanRef a, SpanRef b, SpanRef r)
    {
        w.Line($"// {r.Variable} = {a.Variable} n {b.Variable}");
        w.Line("// Two-pointer sweep. Each emitted span takes its entry from whichever operand entered");
        w.Line("// last and its exit from whichever leaves first -- those are the surfaces actually");
        w.Line("// bounding the result.");
        w.Open($"void intersect_{a.Variable}_{b.Variable}_{r.Variable}()");
        w.Line($"{r.Variable}.count = 0;");
        w.Line();
        w.Line("int i = 0;");
        w.Line("int j = 0;");
        w.Line();
        w.Open($"for (int step = 0; step < {a.Variable}.count + {b.Variable}.count; ++step)");
        w.Line($"if (i >= {a.Variable}.count || j >= {b.Variable}.count) break;");
        w.Line();
        w.Line($"Span x = {a.Variable}.items[i];");
        w.Line($"Span y = {b.Variable}.items[j];");
        w.Line("Span s;");
        w.Line();
        w.Line("int entry;");
        w.Line("int exit_;");
        w.Line();
        w.Line("if (x.tIn > y.tIn) { s.tIn = x.tIn; entry = surfIn(x); }");
        w.Line("else               { s.tIn = y.tIn; entry = surfIn(y); }");
        w.Line();
        w.Line("if (x.tOut < y.tOut) { s.tOut = x.tOut; exit_ = surfOut(x); }");
        w.Line("else                 { s.tOut = y.tOut; exit_ = surfOut(y); }");
        w.Line();
        w.Line("s.surf = packSurf(entry, exit_);");
        w.Line($"PUSH({r.Variable}, s);");
        w.Line();
        w.Line("// Advance past whichever ends first; the other may still meet the next one.");
        w.Line("if (x.tOut < y.tOut) i++; else j++;");
        w.Close();
        w.Close();
        w.Line();
    }

    private static void WriteComplement(GlslWriter w, SpanRef a, SpanRef r)
    {
        w.Line($"// {r.Variable} = complement({a.Variable})");
        w.Line("// The gaps between the spans, extended to +/-infinity, with every surface flipped.");
        w.Line("//");
        w.Line("// The flip is the whole point. Where a surface of the subtracted solid bounds the");
        w.Line("// result, the ray is leaving that solid's interior, so its outward normal points *into*");
        w.Line("// what remains. Negating it is what makes the inside of a drilled hole shade instead of");
        w.Line("// going black -- the single most commonly botched detail in a CSG renderer.");
        w.Open($"void complement_{a.Variable}_{r.Variable}()");
        w.Line($"{r.Variable}.count = 0;");
        w.Line();
        w.Line("float cursor = -INF;");
        w.Line("int   surf   = 0;      // the -infinity end bounds nothing");
        w.Line();
        w.Open($"for (int i = 0; i < {a.Variable}.count; ++i)");
        w.Line("Span gap;");
        w.Line("gap.tIn  = cursor;");
        w.Line($"gap.tOut = {a.Variable}.items[i].tIn;");
        w.Line($"gap.surf = packSurf(surf, -surfIn({a.Variable}.items[i]));");
        w.Line($"PUSH({r.Variable}, gap);");
        w.Line();
        w.Line($"cursor = {a.Variable}.items[i].tOut;");
        w.Line($"surf   = -surfOut({a.Variable}.items[i]);");
        w.Close();
        w.Line();
        w.Line($"PUSH({r.Variable}, Span(cursor, INF, packSurf(surf, 0)));");
        w.Close();
        w.Line();
    }

    private static void WriteResolve(GlslWriter w, SpanRef root)
    {
        w.Line("// The visible surface of one finished shape, folded into the running best.");
        w.Line("//");
        w.Line("// `instance` is which appearance of the shape produced the list, or -1 for a singleton.");
        w.Line("// It is recorded here rather than packed into `surf` because this is the one place that");
        w.Line("// already knows it: the walk that called the shape is the walk that chose the instance.");
        w.Line("// That is what leaves packSurf/surfIn/surfOut -- the largest single speed-up in this");
        w.Line("// renderer's history -- untouched by instancing.");
        w.Open($"void resolve_{root.Variable}(inout Hit best, int instance)");
        w.Open($"for (int i = 0; i < {root.Variable}.count; ++i)");
        w.Line($"Span span = {root.Variable}.items[i];");
        w.Line("if (span.tOut < EPS) continue;   // entirely behind the eye");
        w.Line();
        w.Line("// Spans are sorted, so the first one still ahead is the visible one for this root;");
        w.Line("// whatever it decides, this root has had its say.");
        w.Line("float t;");
        w.Line("int   surf;");
        w.Line("bool  inside;");
        w.Line();
        w.Open("if (span.tIn > EPS)");
        w.Line("t = span.tIn;  surf = surfIn(span);  inside = false;");
        w.Close();
        w.Open("else");
        w.Line("// The ray started inside: the visible surface is where it leaves, seen from");
        w.Line("// behind, so the normal is reversed on top of whatever the encoding says.");
        w.Line("t = span.tOut; surf = surfOut(span); inside = true;");
        w.Close();
        w.Line();
        w.Line("// surf == 0 is an end at infinity: one that survived a complement, or one belonging");
        w.Line("// to a plane, which is unbounded on the far side by construction.");
        w.Open("if (surf != 0 && t < best.t)");
        w.Line("best.found     = true;");
        w.Line("best.t         = t;");
        w.Line("best.primitive = abs(surf) - 1;");
        w.Line("best.instance  = instance;");
        w.Line("best.flip      = (surf < 0) != inside;");
        w.Line("best.entering  = !inside;");
        w.Close();
        w.Line();
        w.Line("return;");
        w.Close();
        w.Close();
        w.Line();
    }

    private static void WriteOccludes(GlslWriter w, SpanRef root)
    {
        w.Open($"bool occludes_{root.Variable}(float maxT)");
        w.Open($"for (int i = 0; i < {root.Variable}.count; ++i)");
        w.Open(
            $"if ({root.Variable}.items[i].tOut > EPS "
            + $"&& {root.Variable}.items[i].tIn < maxT - EPS)");
        w.Line("return true;");
        w.Close();
        w.Close();
        w.Line();
        w.Line("return false;");
        w.Close();
        w.Line();
    }
}
