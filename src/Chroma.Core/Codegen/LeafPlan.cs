using System.Numerics;
using Chroma.Core.Compilation;

namespace Chroma.Core.Codegen;

/// <summary>
/// Everything <see cref="LeafEmitter"/> needs about one leaf, gathered by the visitor that
/// walked the tree so the emitter never has to know what a <c>Lathe</c> is.
/// </summary>
/// <param name="Index">
/// The leaf's index in the scene. It is what a span's surface code names, and what the normal
/// and material lookups index the primitive table by, so it is the one number the generated
/// code and the uploaded buffer both have to agree on.
/// </param>
/// <param name="Spans">Spans this leaf can produce along one ray. Exact, and no longer clamped.</param>
/// <param name="ToLocal">World to the primitive's canonical space, already composed and inverted.</param>
internal readonly record struct LeafPlan(
    int Index,
    PrimitiveKind Kind,
    int Spans,
    Matrix4x4 ToLocal,
    float ParamA,
    IReadOnlyList<Vector2> Points,
    IReadOnlyList<Vector4> Balls,
    IReadOnlyList<float> Strengths,
    string Comment);
