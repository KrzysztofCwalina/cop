using CopMeta;

// copmeta - regenerates the data-driven keyword lists of the TextMate grammar
// (install/vscode-cop/syntaxes/cop.tmLanguage.json) from cop's own C# definitions (tokenizer,
// LanguageMetadata). Run by install/publish.ps1 before packaging the extension. Editor IntelliSense
// is served live by `cop langserver`, so no static metadata file is generated.
//
// Usage:
//   dotnet run --project tools/copmeta [--repo-root <dir>] [--check]
//     --repo-root  Repository root (default: auto-detected by walking up for .git).
//     --check      Verify the committed grammar is up to date; exit 1 if stale (used by tests/CI).

string? repoRoot = null;
bool check = false;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--repo-root" && i + 1 < args.Length) repoRoot = args[++i];
    else if (args[i] == "--check") check = true;
    else { Console.Error.WriteLine($"Unknown argument: {args[i]}"); return 2; }
}

repoRoot ??= FindRepoRoot(Directory.GetCurrentDirectory());
if (repoRoot is null || !File.Exists(Path.Combine(repoRoot, MetadataGenerator.GrammarRelPath.Replace('/', Path.DirectorySeparatorChar))))
{
    Console.Error.WriteLine("Could not locate repository root (no .git with the extension grammar). Pass --repo-root.");
    return 2;
}

try
{
    return MetadataGenerator.Run(repoRoot, check);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"copmeta failed: {ex.Message}");
    return 2;
}

static string? FindRepoRoot(string start)
{
    var dir = new DirectoryInfo(start);
    while (dir is not null)
    {
        if (Directory.Exists(Path.Combine(dir.FullName, ".git")) || File.Exists(Path.Combine(dir.FullName, ".git")))
            return dir.FullName;
        dir = dir.Parent;
    }
    return null;
}
