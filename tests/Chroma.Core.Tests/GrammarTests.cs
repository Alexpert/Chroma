using System.Text.Json;
using System.Text.RegularExpressions;
using Chroma.Core.Sdl.Binding;
using Chroma.Core.Sdl.Lexing;

namespace Chroma.Core.Tests;

/// <summary>
/// The editor grammar's word lists, against the lists they are copies of.
/// </summary>
/// <remarks>
/// <para>
/// <c>editors/vscode/syntaxes/chroma.tmLanguage.json</c> is a TextMate grammar, which is a file
/// of regular expressions and no code: it cannot ask the lexer what the reserved words are, so
/// it names them. That copy is the whole risk of shipping one. A word the lexer reserves and
/// the grammar does not know is coloured as an identifier, which is a wrong answer that looks
/// like an ordinary file rather than like a bug, and the keyword list has already grown twice.
/// </para>
/// <para>
/// So the copy is checked rather than trusted. Each list in the grammar is one match of the
/// shape <c>\b(a|b|c)\b</c>, which is what makes it readable from here; adding a keyword, a
/// built-in or a node type without touching the grammar fails this test with the name that was
/// added.
/// </para>
/// </remarks>
public class GrammarTests
{
    [Fact]
    public void GrammarKnowsExactlyTheReservedWords()
    {
        // Every reserved word is somewhere in the grammar, in the group that says how it is
        // coloured: control flow, declarations, the two booleans, and the two that are still
        // reserved but no longer parse.
        AssertSameWords(
            Lexer.ReservedWords,
            Words("keywords-control", "keywords-declaration", "constants-boolean", "keywords-retired"),
            "reserved words");
    }

    [Fact]
    public void GrammarKnowsExactlyTheBuiltins()
    {
        AssertSameWords(
            Builtins.Names,
            Words("builtin-constants", "builtin-functions"),
            "built-in names");
    }

    [Fact]
    public void GrammarKnowsExactlyTheNodeTypes()
    {
        AssertSameWords(
            NodeBinderRegistry.CreateDefault().Names,
            Words("node-types"),
            "node types");
    }

    private static void AssertSameWords(
        IReadOnlySet<string> expected,
        IReadOnlySet<string> actual,
        string what)
    {
        string missing = string.Join(", ", expected.Except(actual).Order());
        string extra = string.Join(", ", actual.Except(expected).Order());

        Assert.True(
            missing.Length == 0,
            $"the editor grammar does not know these {what}: {missing}. "
            + "Add them to editors/vscode/syntaxes/chroma.tmLanguage.json.");

        Assert.True(
            extra.Length == 0,
            $"the editor grammar colours these as {what} and the language has no such thing: {extra}.");
    }

    /// <summary>The alternation of one or more of the grammar's word lists, as a set.</summary>
    private static IReadOnlySet<string> Words(params string[] keys)
    {
        using JsonDocument grammar = JsonDocument.Parse(File.ReadAllText(GrammarPath()));
        JsonElement repository = grammar.RootElement.GetProperty("repository");
        HashSet<string> words = new(StringComparer.Ordinal);

        foreach (string key in keys)
        {
            Assert.True(
                repository.TryGetProperty(key, out JsonElement rule),
                $"the grammar has no '{key}' rule, which this test reads its word list from.");

            string pattern = rule.GetProperty("match").GetString()!;
            Match alternation = Regex.Match(pattern, @"^\\b\(([^)]*)\)\\b$");

            Assert.True(
                alternation.Success,
                $"'{key}' is not one alternation of the shape \\b(a|b|c)\\b, which is the shape "
                + $"this test reads: {pattern}");

            words.UnionWith(alternation.Groups[1].Value.Split('|'));
        }

        return words;
    }

    /// <summary>
    /// The grammar, found by walking up from the test binary to the folder holding the solution.
    /// </summary>
    private static string GrammarPath()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            string grammar = Path.Combine(
                directory.FullName, "editors", "vscode", "syntaxes", "chroma.tmLanguage.json");

            if (File.Exists(grammar))
            {
                return grammar;
            }
        }

        throw new FileNotFoundException(
            "editors/vscode/syntaxes/chroma.tmLanguage.json is not above " + AppContext.BaseDirectory);
    }
}
