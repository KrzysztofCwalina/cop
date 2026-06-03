using System.CommandLine;
using Cop.Core;
using Cop.Lang;
using Cop.Lang.Ast;
using Cop.Lang.Parser;

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
        var packageDir = PackageResolver.ResolvePackageDir(packageName);

        if (packageDir == null)
        {
            Console.Error.WriteLine($"Error: Package '{packageName}' not found.");
            return 1;
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
        var modules = new List<(string Path, ModuleNode Module)>();
        foreach (var file in copFiles)
        {
            try
            {
                var source = File.ReadAllText(file);
                modules.Add((file, CopParser.Parse(source, file)));
            }
            catch (ParseException ex)
            {
                Console.Error.WriteLine(ex.Message);
            }
        }

        // Collect exported lets and commands
        var lets = new List<(string Name, string? Doc)>();
        var groups = new List<(string Name, string? Doc, int Count)>();
        var commands = new List<(string Name, string? Doc, List<string>? Params)>();

        foreach (var (_, module) in modules)
        {
            foreach (var decl in module.Declarations)
            {
                if (decl is LetDecl let && let.IsExported)
                {
                    if (let.Value is Cop.Lang.Ast.BinaryExpr { Op: BinaryOp.Add })
                    {
                        // Union of collections (a + b + c)
                        var count = CountBinaryAddElements(let.Value);
                        groups.Add((let.Name, let.DocComment, count));
                    }
                    else
                    {
                        lets.Add((let.Name, let.DocComment));
                    }
                }
                else if (decl is CommandDecl cmd && cmd.IsExported)
                {
                    commands.Add((cmd.Name, cmd.DocComment, cmd.Parameters));
                }
                else if (decl is FunctionDecl func && func.IsExported && char.IsUpper(func.Name[0]) && func.Body is BlockBody)
                {
                    commands.Add((func.Name, func.DocComment, func.Params.Select(p => p.Name).ToList()));
                }
            }
        }

        // Print results
        if (lets.Count == 0 && groups.Count == 0 && commands.Count == 0)
        {
            Console.WriteLine($"{packageName} — no exported lets or commands found.");
            return 0;
        }

        // Get package description from first doc comment or file header
        var packageDoc = GetPackageDescription(modules.Select(m => m.Path).ToList(), packageName);
        if (packageDoc != null)
            Console.WriteLine($"{packageName} — {packageDoc}");
        else
            Console.WriteLine(packageName);
        Console.WriteLine();

        if (lets.Count > 0)
        {
            Console.WriteLine("Lets:");
            foreach (var (name, doc) in lets)
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

    private static string? GetPackageDescription(List<string> filePaths, string packageName)
    {
        foreach (var path in filePaths)
        {
            if (path.Contains(packageName, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var lines = File.ReadAllLines(path);
                    if (lines.Length > 0 && lines[0].StartsWith("# "))
                        return lines[0][2..].Trim();
                }
                catch { }
            }
        }
        return null;
    }

    private static int CountBinaryAddElements(Cop.Lang.Ast.Expression expr)
    {
        if (expr is Cop.Lang.Ast.BinaryExpr { Op: BinaryOp.Add } bin)
            return CountBinaryAddElements(bin.Left) + CountBinaryAddElements(bin.Right);
        return 1;
    }
}
