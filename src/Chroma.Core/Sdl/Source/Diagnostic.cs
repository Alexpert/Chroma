namespace Chroma.Core.Sdl.Source;

public enum DiagnosticSeverity
{
    Warning,
    Error,
}

/// <summary>
/// One problem found in a scene file, anchored to the span that caused it.
/// </summary>
/// <remarks>
/// Diagnostics are values, not exceptions. A scene file is written by hand and will
/// contain several mistakes at once; stopping at the first one turns fixing it into a
/// guessing game, so they are collected in a <see cref="DiagnosticBag"/> and reported
/// together.
/// </remarks>
public sealed record Diagnostic(
    SourceText Source,
    SourceSpan Span,
    DiagnosticSeverity Severity,
    string Message)
{
    public (int Line, int Column) Position => Source.GetLineColumn(Span.Start);

    /// <summary>
    /// The conventional compiler format, <c>path:line:column: severity: message</c>,
    /// which editors and terminals already know how to turn into a clickable location.
    /// </summary>
    public override string ToString()
    {
        (int line, int column) = Position;
        string severity = Severity == DiagnosticSeverity.Error ? "error" : "warning";
        return $"{Source.Path}:{line}:{column}: {severity}: {Message}";
    }
}
