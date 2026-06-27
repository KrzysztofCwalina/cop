using CopMeta;

// copmeta - generates install/vscode-cop/metadata.json and the data-driven keyword lists of the
// TextMate grammar from cop's own C# definitions (tokenizer, LanguageMetadata) and the core
// packages loaded with the real parser. Run by install/publish.ps1 before packaging the extension.
//
// Usage:
//   dotnet run --project tools/copmeta [--repo-root <dir>] [--check]
//     --repo-root  Repository root (default: auto-detected by walking up for .git).
//     --check      Verify the committed files are up to date; exit 1 if stale (used by tests/CI).

string? repoRoot = null;
bool check = false;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--repo-root" && i + 1 < args.Length) repoRoot = args[++i];
    else if (args[i] == "--check") check = true;
    else { Console.Error.WriteLine($"Unknown argument: {args[i]}"); return 2; }
}

repoRoot ??= FindRepoRoot(Directory.GetCurrentDirectory());
if (repoRoot is null || !Directory.Exists(Path.Combine(repoRoot, "packages")))
{
    Console.Error.WriteLine("Could not locate repository root (no .git with a packages/ dir). Pass --repo-root.");
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
