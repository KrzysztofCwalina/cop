using System.Text.RegularExpressions;
using Cop.Lang;

namespace CopMeta;

/// <summary>
/// Regenerates the data-driven parts of the TextMate grammar — the keyword/constant lists in
/// <c>install/vscode-cop/syntaxes/cop.tmLanguage.json</c> — from cop's authoritative
/// <see cref="LanguageMetadata"/> (whose keyword names are owned by <see cref="Tokenizer.Keywords"/>),
/// so the colorizer can never drift from the language.
///
/// Editor IntelliSense (hover, completion, diagnostics) is served live by <c>cop langserver</c> from
/// the real compiler, so there is no static editor-metadata file to generate — only the grammar.
/// </summary>
public static class MetadataGenerator
{
    public const string GrammarRelPath = "install/vscode-cop/syntaxes/cop.tmLanguage.json";

    /// <summary>Injects the metadata-derived keyword/constant lists into an existing grammar file.</summary>
    public static string BuildGrammar(string existingGrammar)
    {
        var declaration = LanguageMetadata.Keywords.Where(k => k.Category == "declaration").Select(k => k.Name);
        var control = LanguageMetadata.Keywords.Where(k => k.Category == "control").Select(k => k.Name);
        var constants = LanguageMetadata.Keywords.Where(k => k.Category == "constant").Select(k => k.Name);

        var text = existingGrammar;
        text = ReplaceSectionMatch(text, "declaration-keywords", Alternation(declaration));
        text = ReplaceSectionMatch(text, "control-keywords", Alternation(control));
        text = ReplaceSectionMatch(text, "constants", Alternation(constants));
        return text;
    }

    /// <summary>
    /// Regenerates the grammar keyword lists. When <paramref name="check"/> is true, only verifies the
    /// committed grammar is up to date and returns non-zero (without writing) if not.
    /// </summary>
    public static int Run(string repoRoot, bool check)
    {
        var grammarPath = Path.Combine(repoRoot, GrammarRelPath.Replace('/', Path.DirectorySeparatorChar));
        var newGrammar = BuildGrammar(File.ReadAllText(grammarPath));

        if (check)
        {
            if (Normalize(File.ReadAllText(grammarPath)) != Normalize(newGrammar))
            {
                Console.Error.WriteLine($"STALE: {GrammarRelPath} keyword lists are out of date. Run: dotnet run --project tools/copmeta");
                return 1;
            }
            Console.WriteLine("grammar keyword lists are up to date.");
            return 0;
        }

        File.WriteAllText(grammarPath, newGrammar);
        Console.WriteLine($"Wrote {GrammarRelPath} (keyword lists)");
        return 0;
    }

    private static string Normalize(string s) => s.Replace("\r\n", "\n").TrimEnd('\n') + "\n";

    private static string Alternation(IEnumerable<string> names) =>
        @"\\b(" + string.Join("|", names) + @")\\b";

    /// <summary>Replaces the <c>"match"</c> value of the named grammar repository section.</summary>
    private static string ReplaceSectionMatch(string grammar, string sectionKey, string newMatch)
    {
        // Find: "sectionKey": { ... "match": "<value>"
        var sectionPattern = new Regex(
            "(\"" + Regex.Escape(sectionKey) + "\"\\s*:\\s*\\{[^}]*?\"match\"\\s*:\\s*\")(.*?)(\")",
            RegexOptions.Singleline);
        var m = sectionPattern.Match(grammar);
        if (!m.Success)
            throw new InvalidOperationException($"Grammar section '{sectionKey}' with a 'match' field not found.");
        return grammar[..m.Groups[2].Index] + newMatch + grammar[(m.Groups[2].Index + m.Groups[2].Length)..];
    }
}
