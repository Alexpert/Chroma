using System.Collections;

namespace ChromaTest.Core.Sdl.Source;

/// <summary>
/// Collects the diagnostics produced while loading one scene file. Shared by the lexer,
/// the parser, the evaluator and the binders, so a single run reports everything it can
/// find rather than only the first failure.
/// </summary>
public sealed class DiagnosticBag(SourceText source) : IReadOnlyList<Diagnostic>
{
    private readonly List<Diagnostic> _items = [];

    public SourceText Source { get; } = source;

    public bool HasErrors { get; private set; }

    public int Count => _items.Count;

    public Diagnostic this[int index] => _items[index];

    public void Error(SourceSpan span, string message)
    {
        _items.Add(new Diagnostic(Source, span, DiagnosticSeverity.Error, message));
        HasErrors = true;
    }

    public void Warning(SourceSpan span, string message)
    {
        _items.Add(new Diagnostic(Source, span, DiagnosticSeverity.Warning, message));
    }

    /// <summary>
    /// Diagnostics in source order. The lexer, parser and binder each sweep the file
    /// separately, so the raw insertion order is by phase, which is not how anyone reads
    /// a file.
    /// </summary>
    public IReadOnlyList<Diagnostic> InSourceOrder() =>
        [.. _items.OrderBy(d => d.Span.Start).ThenBy(d => d.Span.Length)];

    public IEnumerator<Diagnostic> GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
