// Lists the commands and declarations available in a Cop program.

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
    /// <summary>
    /// Creates the command for listing a package's exported commands and checks.
    /// </summary>
    public static Command Create()
    {
        Argument<string> packageArg = new Argument<string>("package")
        {
            Description = "Package name to inspect (e.g., csharp-checks)"
        };
        Command command = new Command("commands", "Show checks, groups, and commands exported by a package")
        {
            packageArg
        };
        command.SetAction(parseResult =>
        {
            string packageName = parseResult.GetValue(packageArg)!;
            return Execute(packageName);
        });
        return command;
    }

    /// <summary>Resolves a package and lists its exported commands and checks.</summary>
    public static int Execute(string packageName)
    {
        string? packageDir = PackageResolver.ResolvePackageDir(packageName);

        if (packageDir == null)
        {
            Console.Error.WriteLine($"Error: Package '{packageName}' not found.");
            return 1;
        }

        // Find .cop source files
        string copDir = Path.Combine(packageDir, "src");

        if (!Directory.Exists(copDir))
        {
            Console.Error.WriteLine($"Error: No src/ or types/ directory in package '{packageName}'.");
            return 1;
        }

        string[] copFiles = Directory.GetFiles(copDir, "*.cop");
        if (copFiles.Length == 0)
        {
            Console.Error.WriteLine($"Error: No .cop files found in package '{packageName}'.");
            return 1;
        }

        // Parse all .cop files
        List<(string Path, ModuleNode Module)> modules = new List<(string Path, ModuleNode Module)>();
        foreach (string file in copFiles)
        {
            try
            {
                string source = File.ReadAllText(file);
                modules.Add((file, CopParser.Parse(source, file)));
            }
            catch (ParseException ex)
            {
                Console.Error.WriteLine(ex.Message);
            }
        }

        // Collect exported lets and commands
        List<(string Name, string? Doc)> lets = new List<(string Name, string? Doc)>();
        List<(string Name, string? Doc, int Count)> groups = new List<(string Name, string? Doc, int Count)>();
        List<(string Name, string? Doc, List<string>? Params)> commands = new List<(string Name, string? Doc, List<string>? Params)>();

        foreach ((string _, ModuleNode? module) in modules)
        {
            foreach (Declaration decl in module.Declarations)
            {
                if (decl is LetDecl let && let.IsExported)
                {
                    if (let.Value is Cop.Lang.Ast.BinaryExpr { Op: BinaryOp.Add })
                    {
                        // Union of collections (a + b + c)
                        int count = CountBinaryAddElements(let.Value);
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
                else if (decl is FunctionDecl func && func.IsExported && func.IsCommand)
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
        string? packageDoc = GetPackageDescription(modules.Select(m => m.Path).ToList(), packageName);
        if (packageDoc != null)
        {
            Console.WriteLine($"{packageName} — {packageDoc}");
        }
        else
        {
            Console.WriteLine(packageName);
        }

        Console.WriteLine();

        if (lets.Count > 0)
        {
            Console.WriteLine("Checks:");
            foreach ((string? name, string? doc) in lets)
            {
                if (!string.IsNullOrEmpty(doc))
                {
                    Console.WriteLine($"  {name,-36} {doc}");
                }
                else
                {
                    Console.WriteLine($"  {name}");
                }
            }
            Console.WriteLine();
        }

        if (groups.Count > 0)
        {
            Console.WriteLine("Groups:");
            foreach ((string? name, string? doc, int count) in groups)
            {
                string desc = doc ?? $"Union of {count} checks";
                Console.WriteLine($"  {name,-36} {desc}");
            }
            Console.WriteLine();
        }

        if (commands.Count > 0)
        {
            Console.WriteLine("Commands:");
            foreach ((string? name, string? doc, List<string>? parameters) in commands)
            {
                string displayName = parameters is { Count: > 0 }
                    ? $"{name}({string.Join(", ", parameters)})"
                    : name;
                if (!string.IsNullOrEmpty(doc))
                {
                    Console.WriteLine($"  {displayName,-36} {doc}");
                }
                else
                {
                    Console.WriteLine($"  {displayName}");
                }
            }
        }

        return 0;
    }

    private static string? GetPackageDescription(List<string> filePaths, string packageName)
    {
        foreach (string path in filePaths)
        {
            if (path.Contains(packageName, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    string[] lines = File.ReadAllLines(path);
                    if (lines.Length > 0 && lines[0].StartsWith("# "))
                    {
                        return lines[0][2..].Trim();
                    }
                }
                catch { }
            }
        }
        return null;
    }

    private static int CountBinaryAddElements(Cop.Lang.Ast.Expression expr)
    {
        if (expr is Cop.Lang.Ast.BinaryExpr { Op: BinaryOp.Add } bin)
        {
            return CountBinaryAddElements(bin.Left) + CountBinaryAddElements(bin.Right);
        }

        return 1;
    }
}
