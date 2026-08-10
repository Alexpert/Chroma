using Chroma.Core;
using Chroma.Core.Model;
using Chroma.Core.Sdl.Source;

namespace Chroma.SceneDump;

/// <summary>
/// Parses a scene file and prints what was understood. The renderer's front end, made
/// observable: if the picture is wrong, this answers whether the file was read the way it
/// was meant.
/// </summary>
internal static class Program
{
    private const int ExitSuccess = 0;
    private const int ExitSceneHasErrors = 1;
    private const int ExitBadUsage = 2;

    private static int Main(string[] args)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine("Usage: Chroma.SceneDump <scene-file>");
            return ExitBadUsage;
        }

        string path = args[0];

        // The path is used as given, resolved against the working directory: a scene file
        // is user data named on the command line, not an asset shipped with the binary.
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"error: no such file '{path}'");
            return ExitBadUsage;
        }

        bool loaded;
        Scene? scene;
        IReadOnlyList<Diagnostic> diagnostics;

        try
        {
            loaded = SceneLoader.TryLoad(path, out scene, out diagnostics);
        }
        catch (IOException exception)
        {
            Console.Error.WriteLine($"error: could not read '{path}': {exception.Message}");
            return ExitBadUsage;
        }

        foreach (Diagnostic diagnostic in diagnostics)
        {
            Console.Error.WriteLine(diagnostic.ToString());
        }

        // The null check is redundant with `loaded` by contract, but flow analysis cannot
        // carry [NotNullWhen] across the try block, and silencing it here is cheaper than
        // reshaping the error handling around the compiler.
        if (!loaded || scene is null)
        {
            int errors = diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error);
            Console.Error.WriteLine($"{errors} error{(errors == 1 ? "" : "s")}; scene not loaded.");
            return ExitSceneHasErrors;
        }

        new HierarchyPrinter(Console.Out).PrintScene(scene);
        return ExitSuccess;
    }
}
