# Optimizing the CSG tree before it becomes a shader

Chroma performs **no tree rewriting at all**. `SceneCompiler.Compile` builds a `GeometryEmitter`
and walks the bound tree verbatim, node for node, in the order the scene file wrote it. This
document asks whether that is a missed opportunity, using as its reference a paper that is
entirely about the question:

> Markus Friedrich, Christoph Roch, Sebastian Feld, Carsten Hahn, Pierre-Alain Fayolle.
> *A Flexible Pipeline for the Optimization of CSG Trees.* WSCG 2020.
> `documents_local/A Flexible Pipeline for the Optimization of CSG Trees.pdf`

The short answer: **one stage of that pipeline is worth taking, one is worth taking for a reason
the paper does not have, and the rest is ruled out by facts about this renderer rather than by
taste.** The rest of this document is which, and why.

Nothing here is implemented. This is a research document in the manner of
[csg-raytracing.md](csg-raytracing.md) and [lighting.md](lighting.md): it exists so that the
implementation decisions are argued once instead of discovered twice.

---

## 1. The paper optimizes for something Chroma does not want

The paper's problem statement is explicit (§4): given a solid `S` and a tree `Φ` describing it,
find the tree with the best **editability**, measured by two quantities.

- **Size**, the number of literals and operations.
- **Proximity**, the fraction of operations whose two operands imply point sets that overlap.

Both exist because a human is going to open that tree in a modeller and move things around. A
size-optimal tree has no redundant operands to keep track of; a high-proximity tree behaves
predictably when a subtree is dragged somewhere else.

Neither is Chroma's problem. The artifact a Chroma user edits is the `.chroma` file, and no
compiler pass may touch it. What the compiler holds is the *bound* tree, which nobody reads. Its
editability is worth exactly nothing.

What the bound tree costs is something else, and it arrived with iteration 12. Since per-scene
code generation ([code-generation.md](code-generation.md)) the tree **is** the shader: every node
becomes GLSL, and the driver refuses a program past roughly 65,000 assembly instructions
([gpu-backends.md](gpu-backends.md)). So tree size does matter here, more sharply than in the
paper, but through a mechanism the paper never considers. The paper's own related-work section
sets this aside in one sentence, citing Rossignac's ordered boolean lists as work that improves
rendering time and "does not necessarily help in minimizing the size of the expression or
improving its editability". Chroma sits on the other side of that sentence.

### The four differences that decide everything downstream

| | Friedrich et al. | Chroma |
| --- | --- | --- |
| **Leaves** | Halfspaces drawn from one shared set `H_S`, interchangeable literals | Ten bounded primitives, each carrying its own transform and its own inherited material |
| **Evaluation** | SDF approximation, `min` and `max` (§3.1.2) | Exact ray intervals, Roth spans ([csg-raytracing.md](csg-raytracing.md)) |
| **Emptiness test** | Sampling the tree's SDF over a domain | Nothing exists. `Solid` is pure data by design, and there is no CPU evaluator |
| **Target** | Editability: size and proximity | Generated GLSL against the driver ceiling, then span-list pool storage |

Two consequences of the first row are load-bearing and easy to miss.

**A Chroma leaf is not a literal.** A material is attached to a solid and inherited through
parents, so two spheres of identical geometry with different materials are two different things.
Every method in the paper that factors, shares or cancels literals assumes `h` is `h` wherever it
appears. Here it is not, and any rewrite that merges two leaves has to prove their resolved
materials agree.

**A Chroma leaf is not a halfspace.** `plane` is one. The other nine are bounded solids. This is
not pedantry: the paper's decomposition stage looks for *dominant* halfspaces, those where
`S = h ∩ S` holds identically, and a bounded solid is dominant over `S` only in the rare case
that it contains all of it. Section 5 returns to this.

---

## 2. What the compiler does today, and what it already knows

Worth stating precisely, because the gap is smaller than "no optimization at all" suggests.

`SceneCompiler.Compile` ([SceneCompiler.cs](../src/Chroma.Core/Compilation/SceneCompiler.cs)) does
three things: make an emitter, call `EmitRoot` once per root, and package the result. There is no
pass between the bound scene and the emitter, and no place currently reserved for one.

`GeometryEmitter.EmitOperation`
([GeometryEmitter.cs](../src/Chroma.Core/Codegen/GeometryEmitter.cs)) binarises an n-ary operator
into a **left-associated chain in author order**: `union { a b c }` becomes `(a ∪ b) ∪ c`, always,
whatever the geometry says.

And then the interesting part. The emitter computes an axis-aligned bounding box for **every**
node as it goes:

```csharp
Aabb bounds = name switch
{
    "Union"        => Aabb.Union(accumulated.Bounds, right.Bounds),
    "Intersection" => Aabb.Intersect(accumulated.Bounds, right.Bounds),
    _              => accumulated.Bounds,
};
```

`Aabb.Intersect` returns an inverted box when the two do not overlap, and `Aabb.IsEmpty` reports
it. The comment on that property, written long before this paper was read, states the paper's
first redundancy rule almost word for word:

> Not a degenerate case to be repaired. `intersection { a b }` of two solids whose boxes do not
> overlap is genuinely nothing, and the tape is entitled to say so — every ray then skips the
> subtree outright.

The tape is entitled to say so and never does. `Node.Bounds` has exactly one consumer,
`WriteTraceScene`, which uses it to guard each **root** with a `boundHit` slab test. Every
interior node's box is computed, carried up the tree, and discarded.

So the first stage of the paper's pipeline is one `if` away from existing, and the data it needs
is already being computed. That is the strongest single finding of this comparison.

---

## 3. What the driver counts, and which optimizations can therefore pay

This section comes before the verdicts because it invalidates the obvious reasoning.

[gpu-backends.md](gpu-backends.md) establishes what the ceiling counts: instructions in the
flattened program, after the driver has **inlined every call** and **unrolled every loop with a
compile-time bound**. It also records the most useful negative result the project has:
deduplicating identical leaf bodies cut the generated source by 29% and moved the ceiling from
about 115 primitives to about 118, which is nothing. One body called from thirty-two places is
still thirty-two bodies in the assembly.

A reader could take from that "reducing the source does not help" and stop. That would be wrong,
and the distinction is the whole basis for taking stage one of the paper:

| Change | Survives the inliner? |
| --- | --- |
| Two identical bodies replaced by one body called twice | **No.** Two call sites, two inlined copies |
| A node **deleted from the tree** | **Yes.** No call site, no copy, nothing emitted |

Redundancy removal is the second kind. A subtree proven empty emits no leaf functions, no
operator calls, no pool slots and no span-list types. It is the one source-side change other than
instancing that reduces what the driver counts.

The second thing gpu-backends.md establishes is where `chess-full.chroma`'s instructions actually
are, and it is not in the CSG operators. Deleting all sixty-four board squares, a third of the
scene's primitives, still leaves a program the driver refuses. The cost is in the turned pieces,
whose lathe bodies unroll. **So no amount of CSG tree optimization rescues `chess-full.chroma`,
and this document should not be read as claiming it will.** Instancing did, by noticing that the
thirty-two pieces are six shapes; that is orthogonal to everything here. What tree optimization addresses is
the operator half of the budget, in scenes whose weight is CSG rather than tessellation, and the
pool storage that the C5041 wall was made of.

---

## 4. Verdict, stage by stage

| Paper | Verdict | Why |
| --- | --- | --- |
| §5.1 redundancy removal rules | **Take** | Every rule maps onto an operator Chroma has. Removes nodes outright, which is the change that survives the inliner |
| §5.1.1 hierarchical sampling | **Take later, and price it honestly** | It is what makes the emptiness test nearly exact. It needs a CPU evaluator of Chroma solids, which is a separate project. See section 6 |
| §5.1.2 CIT-based sampling | **Reject** | The paper's own timings (§6.2.1) find it inferior in every configuration tested, on both data sets |
| §5.2 decomposition by dominant halfspaces | **Reject** | Needs the same sampling, and `S = h ∩ S` is nearly never true when the leaves are bounded solids. See section 5 |
| §5.2 proximity sorting of the chain | **Take, for a different reason** | Reassociating an n-ary chain changes which span-list widths a scene instantiates. See section 7 |
| §5.3.1 DCF sampling + Quine-McCluskey | **Reject** | `n` halfspaces give `2ⁿ` canonical intersection terms. The paper's largest model is 171 nodes and Quine-McCluskey already fails on it (§6.3.1). `lattice.chroma` compiles 425 leaves |
| §5.3.1 Espresso | **Reject** | Same wall, and it needs the sampling infrastructure first anyway |
| §5.3.1 Set Cover as a QUBO | **Reject** | Same wall, plus it is the worst of the four for tree size in the paper's own results (§6.3.1), plus it wants annealing hardware |
| §5.3.2 GA-based optimization | **Reject** | Its fitness is geometric score plus proximity plus size, two of which have no Chroma analogue, and the geometric score is sampled |
| §4 the proximity metric | **Reinterpret** | Not a metric here. Its individual failures are diagnostics. See section 8 |

Three of these deserve more than a table row.

---

## 5. Why decomposition does not transfer

The decomposition stage (§5.2) rewrites a solid as a chain of halfspaces that either dominate `S`
or dominate its complement, times a remainder:

```
S = |((...(S_rem ± d₁) ± ...) ± d_n)|
```

It is size-optimal for the part it covers, because each halfspace appears exactly once. It is the
stage that does most of the work in the paper's evaluation: for seven of eleven models the
remaining solid is empty afterwards and no further optimization runs at all (§6.3.1).

It does not transfer, and the reason is the second consequence of section 1. A halfspace is
infinite, so "this halfspace contains all of `S`" is a common accident: half of space is a large
place. A Chroma leaf is a bounded solid, so `S = sphere ∩ S` requires that sphere to contain the
entire solid, which is not something scenes are built out of. `plane` is the one primitive that
would qualify, and scenes use it as a floor, at the top level, unioned rather than intersected.

Taking decomposition would mean paying for the sampling infrastructure of section 6 to find
dominant operands that are not there. If the sampling ever exists for another reason, this can be
revisited cheaply, because detection is the whole of the cost.

---

## 6. Deciding "is this subtree empty?"

Every rule in stage one reduces to one question, as the paper notes: identity of two sets is
emptiness of their symmetric difference, so an emptiness decision is sufficient. There are two
answers available, and they are very different commitments.

### The bounding box, which is free and already here

`Aabb` is approximate in one direction only, and the type's own documentation is emphatic about
it: a box may be larger than the solid it encloses, never smaller. That one-sidedness is exactly
what makes it usable as a proof. If two boxes do not overlap, the solids inside them cannot
overlap either, and the intersection is genuinely empty. **No false positives are possible.**

It is sound and incomplete. Two boxes that overlap prove nothing at all, so an `intersection` of
a sphere and a torus threaded around it, disjoint in fact and overlapping in box, is not detected.
It costs nothing to have, needs nothing built, and reuses `Aabb.Intersect` and `Aabb.IsEmpty`
unchanged.

For unbounded subtrees the sentinel already handles itself: a subtree containing a `plane` gets
`Aabb.Unbounded`, which overlaps everything, so the test simply declines to fire. That is the
correct conservative answer.

### The paper's hierarchical sampling, which is not free

§5.1.1 samples the tree's SDF over the bounding box of its halfspaces, coarse to fine, with two
optimizations that are the interesting part: **early stopping** the moment any sample comes back
non-positive, and a **lookup table of subtrees already proven empty**, since the fixpoint loop
revisits the same subexpressions repeatedly.

Both transfer verbatim. The problem is what they sample. Chroma has no SDF and, more to the
point, no CPU evaluation of its solids at all. This is deliberate and documented at the top of
`Solid`:

> Deliberately pure data — there is no `Intersect` method here. Ray/solid intersection exists
> once, in GLSL, so there is no second implementation to drift out of step with it.

Implementing the paper's sampling means writing that second implementation. An inside-outside
predicate is weaker than a full span function, but for `lathe`, `blob`, `prism` and `sphereSweep`
it is not much weaker, and it would have to agree with the shader or the optimizer will delete
geometry that renders.

**The honest way to price this: it is not a feature of an optimizer, it is a second
implementation of the geometry, and it should be justified by more than one use.** It happens
that a second use is already on the roadmap. The "Beyond → Testing" entry wants a CPU reference
implementation of the span algorithm as another `ISolidVisitor`, so that "the picture looks
wrong" becomes an assertable unit test, and notes it is worth more since iteration 9 will need a
trusted reference whenever it runs. That is the same object. Built once, it serves the tests, the
optimizer, and any future work that needs to know something about a solid without a GPU.

The recommended order follows from this. **Ship the AABB test first**, because it is free and
catches the class of redundancy that generated geometry actually produces. Treat sampling as a
consequence of the CPU reference implementation whenever that is built, not as a reason to build
it.

---

## 7. Reassociation: the adaptation that is ours

The paper's proximity sorting (§5.2, "Improving Proximity") arranges the halfspace chain so that
each operation's operands overlap spatially, for editability. Chroma wants the same
restructuring, for a cost the paper does not have.

`EmitOperation` binarises left-associated, and the span width of a union is the sum of its
operands':

```csharp
"Union"        => accumulated.Spans + right.Spans,
"Intersection" => accumulated.Spans + right.Spans - 1,
_              => accumulated.Spans + right.Spans,   // Difference
```

Take `union { a b c d e f g h }`, eight single-span leaves. Left-associated, the intermediate
widths are 2, 3, 4, 5, 6, 7, 8. Balanced, they are 2, 2, 2, 2, 4, 4, 8. Both end at 8, and both
need seven binary unions. They do not cost the same:

| | Left-associated | Balanced |
| --- | ---: | ---: |
| Distinct `SpanList_N` types | 8 | 4 |
| Distinct operator bodies | 7 | 5 |
| Pool storage, in spans | 35 | 20 |

The pool figures are the ones to look at. `SpanLibrary` emits one struct per distinct width, and
`GeometryEmitter.Take`/`Release` allocate file-scope globals sized to what is live simultaneously
in the deepest single root. Pool storage is what the `error C5041` wall was made of, before
pooling fixed it, and it is still register pressure.

Legality is not in doubt. Union and intersection are associative and commutative on point sets.
An n-ary `difference` reassociates only its subtrahends, since `a - b - c` is `a - (b ∪ c)`. The
one thing to check by rendering rather than by reasoning is that coalescing in `csgUnion` picks
the same surviving surfaces: interior surfaces vanish from the result either way, so the visible
boundary should be identical, and the project already has the tool to prove it, since
`tools/build-manual.ps1 -Check` compares rendered images byte for byte.

**Two honest caveats, stated because the table above looks better than the change probably is.**

The three numbers are derived by hand from the emitter's rules. **Nothing here has been
measured.**

And the effect on the instruction ceiling is likely small, for the reason section 3 gives. The
number of operator *call sites* is unchanged at seven, and the operator loops already bound on
data (`for (int step = 0; step < a.count + b.count; ++step)`), so they do not unroll. What
reassociation removes is text and pool storage, and gpu-backends.md measured that removing text
buys almost nothing at the ceiling. Expect this one to pay in register pressure and therefore in
speed, if it pays at all, and measure before believing either.

---

## 8. Proximity as a diagnostic rather than a metric

The paper's proximity score is recursive: an operator scores 1 when its two operands' point sets
intersect, and the tree's score is the sum over `#Φ`.

The ratio is meaningless here. The individual failures are not. An operator whose operands
provably do not overlap is one of:

- an `intersection`, which is empty, and the scene contains a solid that draws nothing;
- a `difference`, whose subtrahend removes nothing, and the author expected it to;
- a `union`, which is fine and common, since a union of disjoint things is what a union is for.

The first two are almost always mistakes rather than intentions, and this project reports rather
than silently repairs. It also has unusually good machinery to report with: `DiagnosticBag`, plus
`Solid.Origin` for where a solid was written and `Solid.Generator` for the loop that produced it,
which exists precisely so that a message about generated geometry can name the `for` rather than
the thousandth sphere.

So the proposal is that the pass **warns and then removes**, rather than removing quietly:

```
scene.chroma(41,7): warning: this 'intersection' is empty; the bounding boxes of its
                    operands do not overlap, so it draws nothing
scene.chroma(41,7): note: generated by the loop at line 38, iteration 12 of 25
```

Generated geometry is where this earns its place, and it is the paper's own motivation restated:
a hand-written `intersection` that misses is visible on the screen the first time, whereas one
produced by a loop for 3 of 25 iterations is not visible at all.

---

## 9. What an implementation would look like

Sketched only, so that a later plan has somewhere to start.

A pass between the bound `Scene` and the emitter, called from `SceneCompiler.Compile`, written as
an `ISolidVisitor<Solid>` returning a rewritten tree, iterated to a fixpoint the way the paper's
§5.1 does. The rules, in Chroma's operator vocabulary:

| Rule | From |
| --- | --- |
| `intersection` whose operand boxes are disjoint becomes empty | §5.1, rule 1 |
| `union` operand that is empty is dropped | §5.1, empty set rules |
| `intersection` containing an empty operand becomes empty | §5.1 |
| `difference` whose minuend is empty becomes empty | §5.1, via `A \ B = A ∩ ¬B` |
| `difference` subtrahend disjoint from the minuend is dropped | §5.1, same derivation |
| operator left with one operand folds into it, carrying transform and material | not in the paper; an artifact of n-ary operators |
| operator left with no operands becomes empty, and an empty root is dropped | §5.1 |

Two rules the paper has that Chroma does not need. Double complement elimination has no
counterpart: there is no complement node in the model, since `Difference` is a node and the
complement exists only inside the generated GLSL. And universal-set propagation has no source:
nothing in the model produces `W`, since even `plane` is a halfspace rather than everything.

**One structural note worth recording now.** Bounds are currently computed inside the emitter, as
a side effect of emission, from a canonical box per primitive kind plus the folded ancestor
transform. A pass that runs before the emitter needs the same numbers. Duplicating that table
would be two sources of truth for the one thing in this compiler that must never be wrong in the
unsafe direction, so the table should move: a `SolidBounds : ISolidVisitor<Aabb>` that both the
pass and the emitter call. That refactor is the real cost of the pass, and it is small.

---

## 10. Summary

Can Chroma's scenes be optimized with this paper's methods? Partly, and the useful part is the
cheap part.

1. **Stage one, redundancy removal, is worth taking and is nearly free.** The rules map onto
   operators Chroma has, the geometric decision it needs is already computed at every node and
   thrown away, and deleting a node is the one source-side change that survives the driver's
   inliner.
2. **The emptiness test should start at the bounding box.** Sound, incomplete, and free. The
   paper's sampling is the upgrade, and it is gated on a CPU evaluator of Chroma solids that the
   roadmap already wants for testing. Build it for both reasons or for neither.
3. **Reassociating n-ary chains is worth trying, for our reason rather than the paper's.** It cuts
   pool storage and generated types. Measure it before believing it, and do not expect it at the
   instruction ceiling.
4. **The two-level minimization half of the paper is out of reach**, on combinatorics rather than
   on effort: `2ⁿ` canonical terms against scenes of hundreds of leaves, over leaves that are not
   interchangeable literals because they carry materials.
5. **None of this rescues `chess-full.chroma`**, whose instructions are in unrolled lathe bodies
   rather than in CSG operators. Instancing was the answer there, and it has since been built and
   measured: see [gpu-backends.md](gpu-backends.md). That conclusion is unchanged by it, and so is
   this document's, since what tree optimization would address is a different half of the budget.

## Sources

- Friedrich, Roch, Feld, Hahn, Fayolle, *A Flexible Pipeline for the Optimization of CSG Trees*,
  WSCG 2020. The subject of this document; section numbers throughout refer to it.
- Shapiro and Vossler, *Construction and optimization of CSG representations*, CAD 23(1), 1991.
  The source of the paper's decomposition scheme and its formal vocabulary.
- Tilove, *A null-object detection algorithm for constructive solid geometry*, CACM 27(7), 1984.
  What the paper's redundancy removal is based on, and the ancestor of section 6's question.
- Rossignac, *Ordered boolean list (OBL): reducing the footprint for evaluating boolean
  expressions*, IEEE TVCG 17(9), 2011. Cited by the paper as the rendering-time line of work it is
  not part of, which is the line Chroma is on.
