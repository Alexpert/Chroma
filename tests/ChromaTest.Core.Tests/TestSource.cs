using System.Globalization;
using ChromaTest.Core.Compilation;
using ChromaTest.Core.Model;
using ChromaTest.Core.Sdl.Lexing;
using ChromaTest.Core.Sdl.Source;
using ChromaTest.Core.Sdl.Syntax;

namespace ChromaTest.Core.Tests;

/// <summary>
/// Shorthand for driving one stage of the front end from a string.
/// </summary>
internal static class TestSource
{
    /// <summary>
    /// A minimal valid camera. Every scene needs one, and repeating it in each test would
    /// bury the thing under test.
    /// </summary>
    public const string Camera = "camera { position: [0, 0, 5], lookAt: [0, 0, 0] }\n";

    public static (IReadOnlyList<Token> Tokens, DiagnosticBag Diagnostics) Lex(string text)
    {
        SourceText source = new("test.chroma", text);
        DiagnosticBag diagnostics = new(source);
        return (Lexer.Tokenize(source, diagnostics), diagnostics);
    }

    public static (SceneFile File, DiagnosticBag Diagnostics) Parse(string text)
    {
        SourceText source = new("test.chroma", text);
        DiagnosticBag diagnostics = new(source);
        IReadOnlyList<Token> tokens = Lexer.Tokenize(source, diagnostics);
        return (Parser.Parse(tokens, diagnostics), diagnostics);
    }

    /// <summary>Loads a scene with the standard camera prepended.</summary>
    public static (Scene? Scene, IReadOnlyList<Diagnostic> Diagnostics) Load(string body)
    {
        SceneLoader.TryParse("test.chroma", Camera + body, out Scene? scene, out var diagnostics);
        return (scene, diagnostics);
    }

    /// <summary>Loads a scene and fails the test if anything was reported.</summary>
    public static Scene LoadValid(string body)
    {
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) = Load(body);

        Assert.True(
            scene is not null,
            "expected a valid scene, got: " + string.Join("; ", diagnostics.Select(d => d.Message)));

        return scene!;
    }

    /// <summary>Loads and compiles a scene, failing the test if anything was reported.</summary>
    public static CompiledScene CompileValid(string body)
    {
        SceneLoader.TryCompile(
            "test.chroma",
            Camera + body,
            out CompiledScene? compiled,
            out IReadOnlyList<Diagnostic> diagnostics);

        Assert.True(
            compiled is not null,
            "expected a compiled scene, got: " + string.Join("; ", diagnostics.Select(d => d.Message)));

        return compiled!;
    }

    /// <summary>Loads and compiles a scene that is expected to fail.</summary>
    public static (CompiledScene? Compiled, IReadOnlyList<Diagnostic> Diagnostics) Compile(string body)
    {
        SceneLoader.TryCompile(
            "test.chroma",
            Camera + body,
            out CompiledScene? compiled,
            out IReadOnlyList<Diagnostic> diagnostics);

        return (compiled, diagnostics);
    }

    /// <summary>Loads a scene verbatim, without the standard camera.</summary>
    public static (Scene? Scene, IReadOnlyList<Diagnostic> Diagnostics) LoadRaw(string text)
    {
        SceneLoader.TryParse("test.chroma", text, out Scene? scene, out var diagnostics);
        return (scene, diagnostics);
    }

    /// <summary>
    /// Runs an action under a culture that formats numbers with a decimal comma.
    /// </summary>
    /// <remarks>
    /// This is the shape of a bug that never shows up on the developer's machine and
    /// always shows up on someone else's: a scene file writes 1.5, and a culture-sensitive
    /// parse on a French or German machine rejects it.
    /// </remarks>
    public static void InCommaDecimalCulture(Action action)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("fr-FR");

        try
        {
            action();
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
