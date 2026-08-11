namespace Chroma.Core.Codegen;

/// <summary>
/// Emits the span-list types and the boolean operators, one instantiation per size the scene
/// actually uses.
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
/// Only what is asked for is emitted. A scene of spheres and boxes gets <c>SpanList_1</c>,
/// <c>SpanList_2</c> and one union; nothing else exists in its shader at all.
/// </para>
/// <para>
/// The algorithms are transcribed from the hand-written shader they replace, comments and all,
/// with <c>MAX_SPANS</c> replaced by the operand's own size. They are not a reimplementation:
/// the loop bounds are the only thing that differs.
/// </para>
/// </remarks>
internal sealed class SpanLibrary
{
    private readonly SortedSet<int> _sizes = [];
    private readonly SortedSet<(int Left, int Right, int Result)> _unions = [];
    private readonly SortedSet<(int Left, int Right, int Result)> _intersections = [];
    private readonly SortedSet<(int Left, int Right, int Result)> _differences = [];
    private readonly SortedSet<(int Source, int Result)> _complements = [];
    private readonly SortedSet<int> _roots = [];

    public string Type(int spans)
    {
        _sizes.Add(spans);
        return $"SpanList_{spans}";
    }

    public string Union(int left, int right, int result)
    {
        Type(left);
        Type(right);
        Type(result);
        _unions.Add((left, right, result));
        return $"csgUnion_{left}_{right}_{result}";
    }

    public string Intersection(int left, int right, int result)
    {
        Type(left);
        Type(right);
        Type(result);
        _intersections.Add((left, right, result));
        return $"csgIntersection_{left}_{right}_{result}";
    }

    /// <summary>
    /// <c>A \ B == A ∩ complement(B)</c>, so a difference also pulls in a complement of
    /// <c>B</c>'s size plus one and the intersection that consumes it.
    /// </summary>
    public string Difference(int left, int right, int result)
    {
        Type(left);
        Type(right);
        Type(result);
        _complements.Add((right, right + 1));
        Type(right + 1);
        Intersection(left, right + 1, result);
        _differences.Add((left, right, result));
        return $"csgDifference_{left}_{right}_{result}";
    }

    /// <summary>Resolving and occlusion-testing a finished root, per size.</summary>
    public string Root(int spans)
    {
        Type(spans);
        _roots.Add(spans);
        return $"_{spans}";
    }

    public void WriteTo(GlslWriter w)
    {
        w.Line("// --- Span lists ------------------------------------------------------------------");
        w.Line("// One type per size the scene uses, and one copy of each operator per size triple it");
        w.Line("// combines. A leaf that produces a single span holds a single span.");
        w.Line();

        foreach (int n in _sizes)
        {
            w.Line($"struct SpanList_{n} {{ int count; Span items[{n}]; }};");
        }

        w.Line();

        foreach (int n in _sizes)
        {
            WritePush(w, n);
        }

        foreach ((int source, int result) in _complements)
        {
            WriteComplement(w, source, result);
        }

        foreach ((int left, int right, int result) in _unions)
        {
            WriteUnion(w, left, right, result);
        }

        foreach ((int left, int right, int result) in _intersections)
        {
            WriteIntersection(w, left, right, result);
        }

        foreach ((int left, int right, int result) in _differences)
        {
            WriteDifference(w, left, right, result);
        }

        foreach (int n in _roots)
        {
            WriteResolveRoot(w, n);
            WriteRootOccludes(w, n);
        }
    }

    /// <remarks>
    /// The count test is no longer a backstop against a scene the CPU failed to reject: the
    /// list is sized to this node's own worst case, so a dropped span would be an emitter bug
    /// rather than a scene too complicated for the shader.
    /// </remarks>
    private static void WritePush(GlslWriter w, int n)
    {
        w.Open($"void push_{n}(inout SpanList_{n} list, Span span)");
        w.Line("// A ray grazing a sphere tangentially, or hitting a box exactly on an edge, gives");
        w.Line("// tIn == tOut. Keeping those would leave zero-width slivers along every silhouette.");
        w.Line("if (span.tOut - span.tIn < EPS) return;");
        w.Line($"if (list.count >= {n})          return;");
        w.Line();
        w.Line("list.items[list.count] = span;");
        w.Line("list.count++;");
        w.Close();
        w.Line();
    }

    private static void WriteUnion(GlslWriter w, int a, int b, int r)
    {
        w.Line("// Sorted merge with coalescing. Interior surfaces vanish, which is correct: they are");
        w.Line("// no longer on the boundary of the result.");
        w.Open($"void csgUnion_{a}_{b}_{r}(SpanList_{a} a, SpanList_{b} b, out SpanList_{r} result)");
        w.Line("result.count = 0;");
        w.Line();
        w.Line("int  i = 0;");
        w.Line("int  j = 0;");
        w.Line("bool open = false;");
        w.Line("Span current = noSpan();");
        w.Line();
        w.Line("// Bounded rather than while(true): every iteration consumes exactly one input span.");
        w.Open($"for (int step = 0; step < {a + b}; ++step)");
        w.Line("if (i >= a.count && j >= b.count) break;");
        w.Line();
        w.Line("Span next;");
        w.Open("if (j >= b.count || (i < a.count && a.items[i].tIn <= b.items[j].tIn))");
        w.Line("next = a.items[i];");
        w.Line("i++;");
        w.Close();
        w.Open("else");
        w.Line("next = b.items[j];");
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
        w.Line($"push_{r}(result, current);");
        w.Line("current = next;");
        w.Close();
        w.Close();
        w.Line();
        w.Line($"if (open) push_{r}(result, current);");
        w.Close();
        w.Line();
    }

    private static void WriteIntersection(GlslWriter w, int a, int b, int r)
    {
        w.Line("// Two-pointer sweep. Each emitted span takes its entry from whichever operand entered");
        w.Line("// last and its exit from whichever leaves first -- those are the surfaces actually");
        w.Line("// bounding the result.");
        w.Open($"void csgIntersection_{a}_{b}_{r}(SpanList_{a} a, SpanList_{b} b, out SpanList_{r} result)");
        w.Line("result.count = 0;");
        w.Line();
        w.Line("int i = 0;");
        w.Line("int j = 0;");
        w.Line();
        w.Open($"for (int step = 0; step < {a + b}; ++step)");
        w.Line("if (i >= a.count || j >= b.count) break;");
        w.Line();
        w.Line("Span x = a.items[i];");
        w.Line("Span y = b.items[j];");
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
        w.Line($"push_{r}(result, s);");
        w.Line();
        w.Line("// Advance past whichever ends first; the other may still meet the next one.");
        w.Line("if (x.tOut < y.tOut) i++; else j++;");
        w.Close();
        w.Close();
        w.Line();
    }

    private static void WriteComplement(GlslWriter w, int a, int r)
    {
        w.Line("// The gaps between the spans, extended to +/-infinity, with every surface flipped.");
        w.Line("//");
        w.Line("// The flip is the whole point. Where a surface of the subtracted solid bounds the");
        w.Line("// result, the ray is leaving that solid's interior, so its outward normal points *into*");
        w.Line("// what remains. Negating it is what makes the inside of a drilled hole shade instead of");
        w.Line("// going black -- the single most commonly botched detail in a CSG renderer.");
        w.Open($"void csgComplement_{a}_{r}(SpanList_{a} a, out SpanList_{r} result)");
        w.Line("result.count = 0;");
        w.Line();
        w.Line("float cursor = -INF;");
        w.Line("int   surf   = 0;      // the -infinity end bounds nothing");
        w.Line();
        w.Open($"for (int i = 0; i < {a}; ++i)");
        w.Line("if (i >= a.count) break;");
        w.Line();
        w.Line("Span gap;");
        w.Line("gap.tIn  = cursor;");
        w.Line("gap.tOut = a.items[i].tIn;");
        w.Line("gap.surf = packSurf(surf, -surfIn(a.items[i]));");
        w.Line($"push_{r}(result, gap);");
        w.Line();
        w.Line("cursor = a.items[i].tOut;");
        w.Line("surf   = -surfOut(a.items[i]);");
        w.Close();
        w.Line();
        w.Line($"push_{r}(result, Span(cursor, INF, packSurf(surf, 0)));");
        w.Close();
        w.Line();
    }

    private static void WriteDifference(GlslWriter w, int a, int b, int r)
    {
        w.Line("// A \\ B == A n complement(B). One small complement plus the intersection that already");
        w.Line("// exists, instead of a third merge loop with its own way of being subtly wrong.");
        w.Open($"void csgDifference_{a}_{b}_{r}(SpanList_{a} a, SpanList_{b} b, out SpanList_{r} result)");
        w.Line($"SpanList_{b + 1} complement;");
        w.Line($"csgComplement_{b}_{b + 1}(b, complement);");
        w.Line($"csgIntersection_{a}_{b + 1}_{r}(a, complement, result);");
        w.Close();
        w.Line();
    }

    private static void WriteResolveRoot(GlslWriter w, int n)
    {
        w.Line("// The visible surface of one finished root, folded into the running best.");
        w.Open($"void resolveRoot_{n}(SpanList_{n} list, inout Hit best)");
        w.Open($"for (int i = 0; i < {n}; ++i)");
        w.Line("if (i >= list.count) break;");
        w.Line();
        w.Line("Span span = list.items[i];");
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
        w.Line("best.flip      = (surf < 0) != inside;");
        w.Line("best.entering  = !inside;");
        w.Close();
        w.Line();
        w.Line("return;");
        w.Close();
        w.Close();
        w.Line();
    }

    private static void WriteRootOccludes(GlslWriter w, int n)
    {
        w.Open($"bool rootOccludes_{n}(SpanList_{n} list, float maxT)");
        w.Open($"for (int i = 0; i < {n}; ++i)");
        w.Line("if (i >= list.count) break;");
        w.Line();
        w.Open("if (list.items[i].tOut > EPS && list.items[i].tIn < maxT - EPS)");
        w.Line("return true;");
        w.Close();
        w.Close();
        w.Line();
        w.Line("return false;");
        w.Close();
        w.Line();
    }
}
