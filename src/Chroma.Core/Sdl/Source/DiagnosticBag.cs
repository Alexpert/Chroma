using System.Collections;

namespace Chroma.Core.Sdl.Source;

/// <summary>
/// Collects the diagnostics produced while loading one scene file. Shared by the lexer,
/// the parser, the evaluator and the binders, so a single run reports everything it can
/// find rather than only the first failure.
/// </summary>
/// <remarks>
/// "One scene file" became "one scene file and everything it includes" in iteration 8. The
/// bag still belongs to the load rather than to a file: a span names its own file when it
/// has one, so an error inside a fragment is reported against the fragment while the run
/// stays a single sweep with a single exit code.
/// </remarks>
public sealed class DiagnosticBag(SourceText source) : IReadOnlyList<Diagnostic>
{
    private readonly List<Diagnostic> _items = [];

    // First appearance order, so a file's diagnostics stay together and the scene file's
    // come first. Ordering by path instead would sort an included fragment above the file
    // that asked for it, which is not the order anyone reads them in.
    private readonly List<SourceText> _sources = [source];

    /// <summary>The scene file being loaded; the file a span without one belongs to.</summary>
    public SourceText Source { get; } = source;

    public bool HasErrors { get; private set; }

    public int Count => _items.Count;

    public Diagnostic this[int index] => _items[index];

    public void Error(SourceSpan span, string message)
    {
        _items.Add(new Diagnostic(SourceOf(span), span, DiagnosticSeverity.Error, message));
        HasErrors = true;
    }

    public void Warning(SourceSpan span, string message)
    {
        _items.Add(new Diagnostic(SourceOf(span), span, DiagnosticSeverity.Warning, message));
    }

    /// <summary>
    /// Diagnostics in source order: by file in the order the load first reached them, then
    /// by position within each file.
    /// </summary>
    /// <remarks>
    /// The lexer, parser and binder each sweep the file separately, so the raw insertion
    /// order is by phase, which is not how anyone reads a file.
    /// </remarks>
    public IReadOnlyList<Diagnostic> InSourceOrder() =>
    [
        .. _items
            .OrderBy(d => _sources.IndexOf(d.Source))
            .ThenBy(d => d.Span.Start)
            .ThenBy(d => d.Span.Length),
    ];

    public IEnumerator<Diagnostic> GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private SourceText SourceOf(SourceSpan span)
    {
        SourceText source = span.Source ?? Source;

        if (!_sources.Contains(source))
        {
            _sources.Add(source);
        }

        return source;
    }
}
