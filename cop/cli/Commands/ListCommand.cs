using System.CommandLine;
using Cop.Core;
using Cop.Lang;

namespace Cop.Cli.Commands;

/// <summary>
/// Lists exported checks, groups, and commands from a package.
/// </summary>
public static class ListCommand
{
    public static Command Create()
    {
        var packageArg = new Argument<string>("package")
        {
            Description = "Package name to inspect (e.g., csharp-checks)"
        };
        var command = new Command("commands", "Show checks, groups, and commands exported by a package")
        {
            packageArg
        };
        command.SetAction(parseResult =>
        {
            var packageName = parseResult.GetValue(packageArg)!;
            return Execute(packageName);
        });
        return command;
    }

    public static int Execute(string packageName)
    {
        // Find the package in feed paths
        var feedPaths = FindFeedPaths();
        string? packageDir = null;

        foreach (var feed in feedPaths)
        {
            var candidate = ImportResolver.FindPackageDir(Path.GetFullPath(feed), packageName);
            if (candidate != null)
            {
                packageDir = candidate;
                break;
            }
        }

        if (packageDir == null)
        {
            // Try auto-restore
            var cachePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".cop", "packages");
            var restored = RunCommand.AutoRestorePackagesAsync([packageName], cachePath).GetAwaiter().GetResult();
            if (restored)
            {
                packageDir = ImportResolver.FindPackageDir(cachePath, packageName);
            }

            if (packageDir == null)
            {
                Console.Error.WriteLine($"Error: Package '{packageName}' not found.");
                return 1;
            }
        }

        // Find .cop source files
        var copDir = Path.Combine(packageDir, "src");

        if (!Directory.Exists(copDir))
        {
            Console.Error.WriteLine($"Error: No src/ or types/ directory in package '{packageName}'.");
            return 1;
        }

        var copFiles = Directory.GetFiles(copDir, "*.cop");
        if (copFiles.Length == 0)
        {
            Console.Error.WriteLine($"Error: No .cop files found in package '{packageName}'.");
            return 1;
        }

        // Parse all .cop files
        var scriptFiles = new List<ScriptFile>();
        foreach (var file in copFiles)
        {
            try
            {
                var source = File.ReadAllText(file);
                scriptFiles.Add(ScriptParser.Parse(source, file));
            }
            catch (ParseException ex)
            {
                Console.Error.WriteLine(ex.Message);
            }
        }

        // Collect exported lets, categorize as checks vs groups
        var checks = new List<(string Name, string? Doc)>();
        var groups = new List<(string Name, string? Doc, int Count)>();
        var commands = new List<(string Name, string? Doc, List<string>? Params)>();

        var allLets = scriptFiles.SelectMany(sf => sf.LetDeclarations).ToList();

        foreach (var let in allLets)
        {
            if (!let.IsExported) continue;

            if (let.IsCollectionUnion && let.ValueExpression is CollectionUnionExpr union)
            {
                groups.Add((let.Name, let.DocComment, union.Elements.Count));
            }
            else if (IsViolationLet(let))
            {
                checks.Add((let.Name, let.DocComment));
            }
        }

        foreach (var sf in scriptFiles)
        {
            foreach (var cmd in sf.Commands)
            {
                if (!cmd.IsCommand || !cmd.IsExported) continue;
                commands.Add((cmd.Name, cmd.DocComment, cmd.Parameters));
            }
        }

        // Print results
        if (checks.Count == 0 && groups.Count == 0 && commands.Count == 0)
        {
            Console.WriteLine($"{packageName} — no exported checks or commands found.");
            return 0;
        }

        // Get package description from first doc comment or file header
        var packageDoc = GetPackageDescription(scriptFiles, packageName);
        if (packageDoc != null)
            Console.WriteLine($"{packageName} — {packageDoc}");
        else
            Console.WriteLine(packageName);
        Console.WriteLine();

        if (checks.Count > 0)
        {
            Console.WriteLine("Checks:");
            foreach (var (name, doc) in checks)
            {
                if (!string.IsNullOrEmpty(doc))
                    Console.WriteLine($"  {name,-36} {doc}");
                else
                    Console.WriteLine($"  {name}");
            }
            Console.WriteLine();
        }

        if (groups.Count > 0)
        {
            Console.WriteLine("Groups:");
            foreach (var (name, doc, count) in groups)
            {
                var desc = doc ?? $"Union of {count} checks";
                Console.WriteLine($"  {name,-36} {desc}");
            }
            Console.WriteLine();
        }

        if (commands.Count > 0)
        {
            Console.WriteLine("Commands:");
            foreach (var (name, doc, parameters) in commands)
            {
                var displayName = parameters is { Count: > 0 }
                    ? $"{name}({string.Join(", ", parameters)})"
                    : name;
                if (!string.IsNullOrEmpty(doc))
                    Console.WriteLine($"  {displayName,-36} {doc}");
                else
                    Console.WriteLine($"  {displayName}");
            }
        }

        return 0;
    }

    private static bool IsViolationLet(LetDeclaration let)
    {
        if (let.IsValueBinding || let.IsCollectionUnion) return false;
        if (let.Filters.Count == 0) return false;

        var terminal = let.Filters[^1];
        return terminal is CallExpr call && call.Name is "toError" or "toWarning" or "toInfo";
    }

    private static string? GetPackageDescription(List<ScriptFile> scriptFiles, string packageName)
    {
        // Look for a # comment at the top of the main .cop file
        foreach (var sf in scriptFiles)
        {
            if (sf.FilePath.Contains(packageName, StringComparison.OrdinalIgnoreCase))
            {
                // Read first line comment from the file
                try
                {
                    var lines = File.ReadAllLines(sf.FilePath);
                    if (lines.Length > 0 && lines[0].StartsWith("# "))
                        return lines[0][2..].Trim();
                }
                catch { }
            }
        }
        return null;
    }

    private static List<string> FindFeedPaths()
    {
        var paths = new List<string>();

        var cachePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".cop", "packages");
        if (Directory.Exists(cachePath))
            paths.Add(cachePath);

        var dir = Directory.GetCurrentDirectory();
        while (dir != null)
        {
            var packagesDir = Path.Combine(dir, "packages");
            if (Directory.Exists(packagesDir))
                paths.Add(packagesDir);
            dir = Path.GetDirectoryName(dir);
        }
        return paths;
    }
}
