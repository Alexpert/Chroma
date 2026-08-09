using System.Diagnostics.CodeAnalysis;
using ChromaTest.Core.Compilation;
using ChromaTest.Core.Model;
using ChromaTest.Core.Sdl.Binding;
using ChromaTest.Core.Sdl.Source;

namespace ChromaTest.Core;

/// <summary>
/// The one entry point into the language front end, used identically by the scene dump
/// tool and by the renderer.
/// </summary>
public static class SceneLoader
{
    /// <summary>
    /// Loads a scene from disk. The path is used as given and resolved against the current
    /// working directory — a scene file is user data named on the command line, not an
    /// asset shipped next to the executable.
    /// </summary>
    public static bool TryLoad(
        string path,
        [NotNullWhen(true)] out Scene? scene,
        out IReadOnlyList<Diagnostic> diagnostics) =>
        TryParse(SourceText.FromFile(path), out scene, out diagnostics);

    /// <summary>Loads a scene from text already in memory.</summary>
    public static bool TryParse(
        string path,
        string text,
        [NotNullWhen(true)] out Scene? scene,
        out IReadOnlyList<Diagnostic> diagnostics) =>
        TryParse(new SourceText(path, text), out scene, out diagnostics);

    /// <summary>
    /// Loads a scene and compiles it for the GPU in one step, so that syntax errors and
    /// compilation errors come back together and in the same format.
    /// </summary>
    public static bool TryLoadCompiled(
        string path,
        [NotNullWhen(true)] out CompiledScene? compiled,
        out IReadOnlyList<Diagnostic> diagnostics) =>
        TryCompile(SourceText.FromFile(path), out compiled, out diagnostics);

    /// <summary>Loads and compiles from text already in memory.</summary>
    public static bool TryCompile(
        string path,
        string text,
        [NotNullWhen(true)] out CompiledScene? compiled,
        out IReadOnlyList<Diagnostic> diagnostics) =>
        TryCompile(new SourceText(path, text), out compiled, out diagnostics);

    private static bool TryParse(
        SourceText source,
        [NotNullWhen(true)] out Scene? scene,
        out IReadOnlyList<Diagnostic> diagnostics)
    {
        DiagnosticBag bag = new(source);
        scene = SceneBuilder.Build(source, bag);
        diagnostics = bag.InSourceOrder();
        return scene is not null;
    }

    private static bool TryCompile(
        SourceText source,
        [NotNullWhen(true)] out CompiledScene? compiled,
        out IReadOnlyList<Diagnostic> diagnostics)
    {
        DiagnosticBag bag = new(source);
        Scene? scene = SceneBuilder.Build(source, bag);

        compiled = scene is null ? null : SceneCompiler.Compile(scene, bag);
        diagnostics = bag.InSourceOrder();
        return compiled is not null;
    }
}
