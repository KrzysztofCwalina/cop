using System.CommandLine;
using System.CommandLine.Parsing;
using Cop.Lang;
using Cop.Lang.Ast;
using Cop.Lang.Parser;

namespace Cop.Cli.Commands;

public static class HelpCommand
{
    public static Command Create()
    {
        var fileArg = new Argument<string>("file")
        {
            Arity = ArgumentArity.ZeroOrOne,
            Description = ".cop file path (or omit to scan current directory)"
        };
        var command = new Command("help", "List commands defined in a .cop program")
        {
            fileArg
        };
        command.SetAction(parseResult => Execute(parseResult.GetValue(fileArg)));
        return command;
    }

    public static int Execute(string? file)
    {
        // Handle special subcommands
        if (file != null)
        {
            if (file.Equals("language", StringComparison.OrdinalIgnoreCase) ||
                file.Equals("lang", StringComparison.OrdinalIgnoreCase))
            {
                return ExecuteLanguageHelp();
            }

            // If the argument doesn't end in .cop and isn't a file path, treat as package name
            if (!file.EndsWith(".cop", StringComparison.OrdinalIgnoreCase) && !File.Exists(file))
            {
                return ExecutePackageHelp(file);
            }
        }

        // Original behavior: list commands in .cop files
        return ExecuteCommandList(file);
    }

    private static int ExecuteCommandList(string? file)
    {
        string[] filePaths;

        if (file != null)
        {
            var spec = new FileInfo(file);
            if (!spec.Exists) { Console.Error.WriteLine($"Error: File '{spec.FullName}' not found"); return 1; }
            filePaths = [spec.FullName];
        }
        else
        {
            var dir = Directory.GetCurrentDirectory();
            if (!Directory.Exists(dir))
            {
                Console.Error.WriteLine($"Directory not found: {dir}");
                return 1;
            }
            filePaths = Directory.GetFiles(dir, "*.cop", SearchOption.AllDirectories);
        }

        if (filePaths.Length == 0)
        {
            Console.WriteLine("No .cop files found.");
            return 0;
        }

        var commandEntries = new List<(string Name, string? DocComment, List<string>? Parameters)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in filePaths)
        {
            ModuleNode module;
            try
            {
                var source = File.ReadAllText(path);
                module = CopParser.Parse(source, path);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Console.Error.WriteLine(ex.Message);
                continue;
            }

            foreach (var decl in module.Declarations)
            {
                if (decl is CommandDecl cmd)
                {
                    if (!seen.Add(cmd.Name)) continue;
                    commandEntries.Add((cmd.Name, cmd.DocComment, cmd.Parameters));
                }
                else if (decl is FunctionDecl func && char.IsUpper(func.Name[0]) && func.Body is BlockBody)
                {
                    if (!seen.Add(func.Name)) continue;
                    commandEntries.Add((func.Name, func.DocComment, func.Params.Select(p => p.Name).ToList()));
                }
            }
        }

        if (commandEntries.Count == 0)
        {
            Console.WriteLine("No commands defined.");
            return 0;
        }

        Console.WriteLine("Commands:");
        foreach (var (name, doc, parameters) in commandEntries)
        {
            var displayName = parameters is { Count: > 0 }
                ? $"{name}({string.Join(", ", parameters)})"
                : name;

            if (!string.IsNullOrEmpty(doc))
                Console.WriteLine($"  {displayName,-30} {doc}");
            else
                Console.WriteLine($"  {displayName}");
        }

        return 0;
    }

    public static int ExecuteLanguageHelp()
    {
        Console.WriteLine(LanguageReference.Content);
        return 0;
    }

    public static int ExecutePackageHelp(string packageName)
    {
        // Search for the package in common locations
        var cwd = Directory.GetCurrentDirectory();
        string? packageDir = null;

        // 1. Local restored packages: .cop/packages/<name>/
        var localPackage = Path.Combine(cwd, ".cop", "packages", packageName);
        if (Directory.Exists(localPackage))
            packageDir = localPackage;

        // 2. Local packages/ directory (for package repos)
        if (packageDir == null)
        {
            var repoPackage = Path.Combine(cwd, "packages", packageName);
            if (Directory.Exists(repoPackage))
                packageDir = repoPackage;
        }

        // 3. Search parent directories for packages/ (monorepo)
        if (packageDir == null)
        {
            var dir = Directory.GetParent(cwd);
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "packages", packageName);
                if (Directory.Exists(candidate))
                {
                    packageDir = candidate;
                    break;
                }
                dir = dir.Parent;
            }
        }

        if (packageDir == null)
        {
            Console.Error.WriteLine($"Package '{packageName}' not found.");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Searched:");
            Console.Error.WriteLine($"  .cop/packages/{packageName}/");
            Console.Error.WriteLine($"  packages/{packageName}/");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Try: cop package restore   (to download packages from feeds)");
            return 1;
        }

        return PrintPackageHelp(packageName, packageDir);
    }

    private static int PrintPackageHelp(string packageName, string packageDir)
    {
        Console.WriteLine($"# {packageName}");
        Console.WriteLine();

        // Read README.md if present
        var readmePath = Path.Combine(packageDir, "README.md");
        if (File.Exists(readmePath))
        {
            var readme = File.ReadAllText(readmePath).Trim();
            // Skip the first line if it's just "# package-name"
            var lines = readme.Split('\n');
            int startLine = 0;
            if (lines.Length > 0 && lines[0].TrimStart().StartsWith($"# {packageName}", StringComparison.OrdinalIgnoreCase))
                startLine = 1;
            for (int i = startLine; i < lines.Length; i++)
                Console.WriteLine(lines[i].TrimEnd());
            Console.WriteLine();
        }

        // Parse .cop source files for exports
        var srcDir = Path.Combine(packageDir, "src");
        if (!Directory.Exists(srcDir))
        {
            // Maybe the .cop files are at the root
            srcDir = packageDir;
        }

        var copFiles = Directory.GetFiles(srcDir, "*.cop", SearchOption.TopDirectoryOnly);
        if (copFiles.Length == 0)
        {
            Console.WriteLine("(No .cop source files found)");
            return 0;
        }

        var types = new List<(string Name, string? Doc, List<string> Properties)>();
        var predicates = new List<(string Name, string? Doc, string ParamType)>();
        var functions = new List<(string Name, string? Doc, string Signature)>();
        var commands = new List<(string Name, string? Doc)>();
        var enums = new List<(string Name, string? Doc, string Members)>();
        var flags = new List<(string Name, string? Doc, string Members)>();

        foreach (var copFile in copFiles)
        {
            try
            {
                var source = File.ReadAllText(copFile);
                var module = CopParser.Parse(source, copFile);

                foreach (var decl in module.Declarations)
                {
                    switch (decl)
                    {
                        case TypeDecl td when td.IsExported:
                            var props = td.Properties.Select(p =>
                            {
                                var typeStr = $" : {FormatTypeRef(p.Type, p.IsOptional)}";
                                return $"  {p.Name}{typeStr}";
                            }).ToList();
                            types.Add((td.Name, td.DocComment, props));
                            break;

                        case FunctionDecl fd when fd.IsExported && fd.IsPredicate:
                            var paramType = fd.Params.Count > 0 && fd.Params[0].Type != null
                                ? fd.Params[0].Type.Name : "object";
                            predicates.Add((fd.Name, fd.DocComment, paramType));
                            break;

                        case FunctionDecl fd2 when fd2.IsExported:
                            var sig = FormatFunctionSignature(fd2);
                            if (fd2.Body is BlockBody)
                                commands.Add((fd2.Name, fd2.DocComment));
                            else
                                functions.Add((fd2.Name, fd2.DocComment, sig));
                            break;

                        case CommandDecl cd when cd.IsExported:
                            commands.Add((cd.Name, cd.DocComment));
                            break;

                        case EnumDecl ed when ed.IsExported:
                            var members = string.Join(" | ", ed.Members);
                            enums.Add((ed.Name, ed.DocComment, members));
                            break;

                        case FlagsDecl fld when fld.IsExported:
                            var flagMembers = string.Join(" | ", fld.Members);
                            flags.Add((fld.Name, fld.DocComment, flagMembers));
                            break;
                    }
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Skip unparseable files
            }
        }

        // Print exports
        if (enums.Count > 0)
        {
            Console.WriteLine("## Enums");
            Console.WriteLine();
            foreach (var (name, doc, members) in enums)
            {
                Console.Write($"  enum {name} = {members}");
                if (doc != null) Console.Write($"    # {doc}");
                Console.WriteLine();
            }
            Console.WriteLine();
        }

        if (flags.Count > 0)
        {
            Console.WriteLine("## Flags");
            Console.WriteLine();
            foreach (var (name, doc, members) in flags)
            {
                Console.Write($"  flags {name} = {members}");
                if (doc != null) Console.Write($"    # {doc}");
                Console.WriteLine();
            }
            Console.WriteLine();
        }

        if (types.Count > 0)
        {
            Console.WriteLine("## Types");
            Console.WriteLine();
            foreach (var (name, doc, props) in types)
            {
                if (doc != null)
                    Console.WriteLine($"  ## {doc}");
                Console.WriteLine($"  type {name} = {{");
                foreach (var prop in props)
                    Console.WriteLine($"  {prop}");
                Console.WriteLine("  }");
                Console.WriteLine();
            }
        }

        if (predicates.Count > 0)
        {
            Console.WriteLine("## Predicates");
            Console.WriteLine();
            foreach (var (name, doc, paramType) in predicates)
            {
                Console.Write($"  predicate {name}({paramType})");
                if (doc != null) Console.Write($"    # {doc}");
                Console.WriteLine();
            }
            Console.WriteLine();
        }

        if (functions.Count > 0)
        {
            Console.WriteLine("## Functions");
            Console.WriteLine();
            foreach (var (name, doc, sig) in functions)
            {
                Console.Write($"  {sig}");
                if (doc != null) Console.Write($"    # {doc}");
                Console.WriteLine();
            }
            Console.WriteLine();
        }

        if (commands.Count > 0)
        {
            Console.WriteLine("## Commands");
            Console.WriteLine();
            foreach (var (name, doc) in commands)
            {
                Console.Write($"  {name}");
                if (doc != null) Console.Write($"    # {doc}");
                Console.WriteLine();
            }
            Console.WriteLine();
        }

        // Print samples if available
        var samplesDir = Path.Combine(packageDir, "samples");
        if (Directory.Exists(samplesDir))
        {
            var sampleFiles = Directory.GetFiles(samplesDir, "*.cop", SearchOption.TopDirectoryOnly);
            if (sampleFiles.Length > 0)
            {
                Console.WriteLine("## Samples");
                Console.WriteLine();
                foreach (var sampleFile in sampleFiles)
                {
                    var sampleName = Path.GetFileNameWithoutExtension(sampleFile);
                    Console.WriteLine($"### {sampleName}");
                    Console.WriteLine();
                    Console.WriteLine("```cop");
                    Console.WriteLine(File.ReadAllText(sampleFile).TrimEnd());
                    Console.WriteLine("```");
                    Console.WriteLine();
                }
            }
        }

        return 0;
    }

    private static string FormatFunctionSignature(FunctionDecl fd)
    {
        var paramParts = fd.Params.Select(p =>
        {
            if (p.Type != null)
                return $"{p.Name}: {FormatTypeRef(p.Type, false)}";
            return p.Name;
        });
        var returnType = fd.ReturnType != null ? $" : {FormatTypeRef(fd.ReturnType, false)}" : "";
        return $"function {fd.Name}({string.Join(", ", paramParts)}){returnType}";
    }

    private static string FormatTypeRef(TypeRef tr, bool isOptional)
    {
        var name = tr.IsCollection ? $"[{tr.Name}]" : tr.Name;
        return isOptional ? $"{name}?" : name;
    }
}
