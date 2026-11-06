// Resolves help topics and renders package and language documentation.

using System.CommandLine;
using System.CommandLine.Parsing;
using Cop.Lang;
using Cop.Lang.Ast;
using Cop.Lang.Parser;

namespace Cop.Cli.Commands;

/// <summary>Displays CLI usage and documentation for source files and packages.</summary>
public static class HelpCommand
{
    /// <summary>Banner shown at the top of usage/help output, e.g. "agent cop 2026.6.24.3".</summary>
    public static string Banner =>
        $"agent cop {System.Reflection.Assembly.GetExecutingAssembly().GetName().Version}";

    /// <summary>Creates the help command with an optional source-file argument.</summary>
    public static Command Create()
    {
        Argument<string> fileArg = new Argument<string>("file")
        {
            Arity = ArgumentArity.ZeroOrOne,
            Description = ".cop file path (or omit to scan current directory)"
        };
        Command command = new Command("help", "List commands defined in a .cop program")
        {
            fileArg
        };
        command.SetAction(parseResult => ExecuteAsync(parseResult.GetValue(fileArg)));
        return command;
    }

    /// <summary>
    /// Displays declarations from a source file or the current directory.
    /// </summary>
    public static async Task<int> ExecuteAsync(string? file)
    {
        // Bare "cop help" with no argument — show general help
        if (file == null)
        {
            CliHelp.PrintMainHelp();
            return 0;
        }

        // Handle special subcommands
        if (file.Equals("language", StringComparison.OrdinalIgnoreCase) ||
            file.Equals("lang", StringComparison.OrdinalIgnoreCase))
        {
            return ExecuteLanguageHelp();
        }

        // If the argument doesn't end in .cop and isn't a file path, treat as package name
        if (!file.EndsWith(".cop", StringComparison.OrdinalIgnoreCase) && !File.Exists(file))
        {
            return await ExecutePackageHelpAsync(file);
        }

        // Explicit file: list commands in that .cop file
        return ExecuteCommandList(file);
    }

    private static int ExecuteCommandList(string? file)
    {
        string[] filePaths;

        if (file != null)
        {
            FileInfo spec = new FileInfo(file);
            if (!spec.Exists) { Console.Error.WriteLine($"Error: File '{spec.FullName}' not found"); return 1; }
            filePaths = [spec.FullName];
        }
        else
        {
            string dir = Directory.GetCurrentDirectory();
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

        List<(string Name, string? DocComment, List<string>? Parameters)> commandEntries = new List<(string Name, string? DocComment, List<string>? Parameters)>();
        HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string path in filePaths)
        {
            ModuleNode module;
            try
            {
                string source = File.ReadAllText(path);
                module = CopParser.Parse(source, path);
            }
            catch (Exception ex) when (ex is ParseException or IOException or
                UnauthorizedAccessException)
            {
                Console.Error.WriteLine(ex.Message);
                continue;
            }

            foreach (Declaration decl in module.Declarations)
            {
                if (decl is CommandDecl cmd)
                {
                    if (!seen.Add(cmd.Name))
                    {
                        continue;
                    }

                    commandEntries.Add((cmd.Name, cmd.DocComment, cmd.Parameters));
                }
                else if (decl is FunctionDecl func && func.IsCommand)
                {
                    if (!seen.Add(func.Name))
                    {
                        continue;
                    }

                    commandEntries.Add((func.Name, func.DocComment, func.Params.Select(p => p.Name).ToList()));
                }
            }
        }

        if (commandEntries.Count == 0)
        {
            Console.WriteLine("No commands defined.");
            Console.WriteLine();
            PrintHelpFooter();
            return 0;
        }

        bool color = ConsoleMarkdown.UseColor;
        if (color)
        {
            Console.WriteLine($"{ConsoleMarkdown.Bold}Commands:{ConsoleMarkdown.Reset}");
        }
        else
        {
            Console.WriteLine("Commands:");
        }

        foreach ((string? name, string? doc, List<string>? parameters) in commandEntries)
        {
            string displayName = parameters is { Count: > 0 }
                ? $"{name}({string.Join(", ", parameters)})"
                : name;

            if (color)
            {
                string styledName = $"{ConsoleMarkdown.Bold}{displayName}{ConsoleMarkdown.Reset}";
                if (!string.IsNullOrEmpty(doc))
                {
                    Console.WriteLine($"  {styledName,-42} {ConsoleMarkdown.Gray}{doc}{ConsoleMarkdown.Reset}");
                }
                else
                {
                    Console.WriteLine($"  {styledName}");
                }
            }
            else
            {
                if (!string.IsNullOrEmpty(doc))
                {
                    Console.WriteLine($"  {displayName,-30} {doc}");
                }
                else
                {
                    Console.WriteLine($"  {displayName}");
                }
            }
        }

        Console.WriteLine();
        PrintHelpFooter();
        return 0;
    }

    private static void PrintHelpFooter()
    {
        bool color = ConsoleMarkdown.UseColor;
        if (color)
        {
            Console.WriteLine($"{ConsoleMarkdown.Gray}Other help commands:{ConsoleMarkdown.Reset}");
            Console.WriteLine($"  {ConsoleMarkdown.Cyan}cop help <package>{ConsoleMarkdown.Reset}     Show exports from an imported package");
            Console.WriteLine($"  {ConsoleMarkdown.Cyan}cop help language{ConsoleMarkdown.Reset}      Full language reference");
        }
        else
        {
            Console.WriteLine("Other help commands:");
            Console.WriteLine("  cop help <package>     Show exports from an imported package");
            Console.WriteLine("  cop help language      Full language reference");
        }
    }

    /// <summary>Prints the embedded language reference.</summary>
    public static int ExecuteLanguageHelp()
    {
        ConsoleMarkdown.WriteMarkdown(LanguageReference.Content);
        return 0;
    }

    /// <summary>Resolves a package and prints its exported API documentation.</summary>
    public static async Task<int> ExecutePackageHelpAsync(string packageName)
    {
        List<string> feedPaths = PackageResolver.GetFeedPaths();
        string? packageDir = await PackageResolver.ResolvePackageDirAsync(packageName, feedPaths);

        if (packageDir == null)
        {
            Console.Error.WriteLine($"Package '{packageName}' not found.");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Searched:");
            foreach (string fp in feedPaths)
            {
                Console.Error.WriteLine($"  {fp}");
            }

            if (feedPaths.Count == 0)
            {
                Console.Error.WriteLine("  (no package directories found)");
            }

            Console.Error.WriteLine();
            Console.Error.WriteLine("Try: cop package restore   (to download packages from feeds)");
            return 1;
        }

        return PrintPackageHelp(packageName, packageDir);
    }

    private static int PrintPackageHelp(string packageName, string packageDir)
    {
        bool color = ConsoleMarkdown.UseColor;

        // Title and description
        string? description = null;
        string manifestPath = Path.Combine(packageDir, $"{packageName}.md");
        if (File.Exists(manifestPath))
        {
            description = ExtractFrontmatterField(File.ReadAllText(manifestPath), "description");
        }

        if (color)
        {
            Console.Write($"{ConsoleMarkdown.Bold}{ConsoleMarkdown.Cyan}{packageName}{ConsoleMarkdown.Reset}");
            if (description != null)
            {
                Console.Write($"  {ConsoleMarkdown.Gray}{description}{ConsoleMarkdown.Reset}");
            }
        }
        else
        {
            Console.Write(packageName);
            if (description != null)
            {
                Console.Write($"  {description}");
            }
        }
        Console.WriteLine();
        Console.WriteLine();

        // Parse .cop source files for exports
        string srcDir = Path.Combine(packageDir, "src");
        if (!Directory.Exists(srcDir))
        {
            // Maybe the .cop files are at the root
            srcDir = packageDir;
        }

        string[] copFiles = Directory.GetFiles(srcDir, "*.cop", SearchOption.TopDirectoryOnly);
        if (copFiles.Length == 0)
        {
            Console.WriteLine("(No .cop source files found)");
            return 0;
        }

        List<(string Name, string? Doc, string? BaseType, List<string> Properties)> types = new List<(string Name, string? Doc, string? BaseType, List<string> Properties)>();
        List<(string Name, string? Doc, string ParamType)> predicates = new List<(string Name, string? Doc, string ParamType)>();
        List<(string Name, string? Doc, string Signature)> functions = new List<(string Name, string? Doc, string Signature)>();
        List<(string Name, string? Doc)> commands = new List<(string Name, string? Doc)>();
        List<(string Name, string? Doc, string Members)> enums = new List<(string Name, string? Doc, string Members)>();
        List<(string Name, string? Doc, string Members)> flags = new List<(string Name, string? Doc, string Members)>();
        List<(string Name, string? Doc, string? TypeStr)> lets = new List<(string Name, string? Doc, string? TypeStr)>();

        // Track a language-specific narrowing subtype (a `type XType = Type & {...}`) and its
        // `asXxx` narrowing predicate, so language packages get a teaching section explaining
        // when to reach for language-specific checks.
        string? narrowType = null;
        string? narrowPredicate = null;

        foreach (string copFile in copFiles)
        {
            try
            {
                string source = File.ReadAllText(copFile);
                ModuleNode module = CopParser.Parse(source, copFile);

                foreach (Declaration decl in module.Declarations)
                {
                    switch (decl)
                    {
                        case TypeDecl td when td.IsExported:
                            List<string> props = td.Properties.Select(p =>
                            {
                                string typeStr = $" : {FormatTypeRef(p.Type, p.IsOptional)}";
                                return $"  {p.Name}{typeStr}";
                            }).ToList();
                            types.Add((td.Name, td.DocComment, td.BaseType, props));
                            if (td.BaseType == "Type")
                            {
                                narrowType = td.Name;
                            }

                            break;

                        case FunctionDecl fd when fd.IsExported && fd.IsPredicate:
                            string paramType = fd.Params.Count > 0 &&
                                fd.Params[0].Type is { } parameterType
                                ? parameterType.Name : "object";
                            predicates.Add((fd.Name, fd.DocComment, paramType));
                            if (fd.Name.Length > 2 && fd.Name.StartsWith("as", StringComparison.Ordinal) && char.IsUpper(fd.Name[2]))
                            {
                                narrowPredicate = fd.Name;
                            }

                            break;

                        case FunctionDecl fd2 when fd2.IsExported:
                            string sig = FormatFunctionSignature(fd2);
                            if (fd2.Body is BlockBody)
                            {
                                commands.Add((fd2.Name, fd2.DocComment));
                            }
                            else
                            {
                                functions.Add((fd2.Name, fd2.DocComment, sig));
                            }

                            break;

                        case CommandDecl cd when cd.IsExported:
                            commands.Add((cd.Name, cd.DocComment));
                            break;

                        case EnumDecl ed when ed.IsExported:
                            string members = string.Join(" | ", ed.Members);
                            enums.Add((ed.Name, ed.DocComment, members));
                            break;

                        case FlagsDecl fld when fld.IsExported:
                            string flagMembers = string.Join(" | ", fld.Members);
                            flags.Add((fld.Name, fld.DocComment, flagMembers));
                            break;

                        case LetDecl ld when ld.IsExported:
                            string? letType = ld.TypeAnnotation != null
                                ? FormatTypeRef(ld.TypeAnnotation, false) : null;
                            lets.Add((ld.Name, ld.DocComment, letType));
                            break;
                    }
                }
            }
            catch (Exception ex) when (ex is ParseException or IOException or
                UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"Cannot read package source '{copFile}': {ex.Message}");
            }
        }

        if (packageName == "code" || narrowPredicate is not null)
        {
            ConsoleMarkdown.WriteHeader("Structural check authoring");
            Console.WriteLine("  Use parsed Types/Methods, Calls, modifiers, names, and Parent/Children/Ancestors.");
            Console.WriteLine("  Run 'cop help code' for the common model; use language-specific fields only when needed.");
            Console.WriteLine("  Never reconstruct syntax from Source/Text, Condition/Expression, or string Arguments.");
            Console.WriteLine("  This applies to equality, substring checks, splitting, implicit text conversion, and regex.");
            Console.WriteLine("  If a fact is missing, explain the gap and ASK THE USER before writing any text-based check.");
            Console.WriteLine("  Verify: cop verify cop-checks/ --strict-structure");
            Console.WriteLine("  Only after user approval: add --allow-text-check <approved-name>.");
            Console.WriteLine();
        }

        // Print samples first if available
        string samplesDir = Path.Combine(packageDir, "samples");
        if (Directory.Exists(samplesDir))
        {
            string[] sampleFiles = Directory.GetFiles(samplesDir, "*.cop", SearchOption.TopDirectoryOnly);
            if (sampleFiles.Length > 0)
            {
                ConsoleMarkdown.WriteHeader("Samples");
                Console.WriteLine();
                foreach (string sampleFile in sampleFiles)
                {
                    string sampleName = Path.GetFileNameWithoutExtension(sampleFile);
                    ConsoleMarkdown.WriteHeader(sampleName, 3);
                    Console.WriteLine();
                    string sampleContent = File.ReadAllText(sampleFile).TrimEnd();
                    WriteCopSource(sampleContent);
                    Console.WriteLine();
                    Console.WriteLine();
                }
            }
        }

        // Print exports
        if (lets.Count > 0)
        {
            ConsoleMarkdown.WriteHeader("Collections");
            Console.WriteLine();
            foreach ((string? name, string? doc, string? typeStr) in lets)
            {
                Console.Write("  ");
                if (color)
                {
                    Console.Write($"{ConsoleMarkdown.Bold}{name}{ConsoleMarkdown.Reset}");
                }
                else
                {
                    Console.Write(name);
                }

                if (typeStr != null)
                {
                    ConsoleMarkdown.WriteTypeAnnotation(typeStr);
                }

                if (doc != null) { Console.Write("    "); ConsoleMarkdown.WriteDocComment(doc); }
                Console.WriteLine();
            }
            Console.WriteLine();
        }

        if (functions.Count > 0)
        {
            ConsoleMarkdown.WriteHeader("Functions");
            Console.WriteLine();
            foreach ((string? name, string? doc, string? sig) in functions)
            {
                Console.Write("  ");
                WriteColoredSignature(sig);
                if (doc != null) { Console.Write("    "); ConsoleMarkdown.WriteDocComment(doc); }
                Console.WriteLine();
            }
            Console.WriteLine();
        }

        if (predicates.Count > 0)
        {
            ConsoleMarkdown.WriteHeader("Predicates");
            Console.WriteLine();
            foreach ((string? name, string? doc, string? paramType) in predicates)
            {
                Console.Write("  ");
                ConsoleMarkdown.WriteKeywordName("predicate", $"{name}({paramType})");
                if (doc != null) { Console.Write("    "); ConsoleMarkdown.WriteDocComment(doc); }
                Console.WriteLine();
            }
            Console.WriteLine();
        }

        if (types.Count > 0)
        {
            ConsoleMarkdown.WriteHeader("Types");
            Console.WriteLine();
            foreach ((string? name, string? doc, string? baseTypeName, List<string>? props) in types)
            {
                if (doc != null)
                {
                    Console.Write("  ");
                    ConsoleMarkdown.WriteDocComment(doc);
                    Console.WriteLine();
                }
                Console.Write("  ");
                ConsoleMarkdown.WriteKeywordName("type", name);
                // Show the base type for intersection subtypes, e.g. `CSharpType = Type & { ... }`.
                string opener = baseTypeName != null ? $" = {baseTypeName} &" : " =";
                if (props.Count == 0)
                {
                    Console.WriteLine($"{opener} {{}}");
                }
                else
                {
                    Console.WriteLine($"{opener} {{");
                    foreach (string prop in props)
                    {
                        WritePropertyLine("    " + prop.TrimStart());
                    }

                    Console.WriteLine("  }");
                }
                Console.WriteLine();
            }
        }

        if (enums.Count > 0)
        {
            ConsoleMarkdown.WriteHeader("Enums");
            Console.WriteLine();
            foreach ((string? name, string? doc, string? members) in enums)
            {
                Console.Write("  ");
                ConsoleMarkdown.WriteKeywordName("enum", name);
                Console.Write($" = {members}");
                if (doc != null) { Console.Write("    "); ConsoleMarkdown.WriteDocComment(doc); }
                Console.WriteLine();
            }
            Console.WriteLine();
        }

        if (flags.Count > 0)
        {
            ConsoleMarkdown.WriteHeader("Flags");
            Console.WriteLine();
            foreach ((string? name, string? doc, string? members) in flags)
            {
                Console.Write("  ");
                ConsoleMarkdown.WriteKeywordName("flags", name);
                Console.Write($" = {members}");
                if (doc != null) { Console.Write("    "); ConsoleMarkdown.WriteDocComment(doc); }
                Console.WriteLine();
            }
            Console.WriteLine();
        }

        if (commands.Count > 0)
        {
            ConsoleMarkdown.WriteHeader("Commands");
            Console.WriteLine();
            foreach ((string? name, string? doc) in commands)
            {
                Console.Write("  ");
                if (color)
                {
                    Console.Write($"{ConsoleMarkdown.Bold}{name}{ConsoleMarkdown.Reset}");
                }
                else
                {
                    Console.Write(name);
                }

                if (doc != null) { Console.Write("    "); ConsoleMarkdown.WriteDocComment(doc); }
                Console.WriteLine();
            }
            Console.WriteLine();
        }

        // Language-specific checks guidance (printed for language packages that define a
        // `type XType = Type & {...}` narrowing subtype).
        if (narrowType != null && narrowPredicate != null)
        {
            ConsoleMarkdown.WriteHeader("Language-specific checks");
            Console.WriteLine();
            Console.WriteLine("  Prefer the language-agnostic model — codebase.Types, Type.Name, Type.Kind,");
            Console.WriteLine("  Type.Modifiers, Type.BaseTypes, etc. — so a check works across languages.");
            Console.WriteLine();
            Console.WriteLine($"  ONLY when a fact cannot be expressed that way, narrow with :{narrowPredicate}");
            Console.WriteLine($"  and read the language-specific fields listed above. The same :{narrowPredicate}");
            Console.WriteLine("  narrows Types, Methods, AND Statements (control blocks, error handling, ...):");
            Console.WriteLine();
            Console.WriteLine($"    codebase.Types:{narrowPredicate}:<predicate>:toError('...')");
            Console.WriteLine($"    codebase.Methods:{narrowPredicate}:<predicate>:toError('...')");
            Console.WriteLine($"    codebase.Statements:{narrowPredicate}:<predicate>:toError('...')");
            Console.WriteLine();
        }
        else if (string.Equals(packageName, "code", StringComparison.OrdinalIgnoreCase))
        {
            // The hub package: point agents at the per-language narrowings.
            ConsoleMarkdown.WriteHeader("Language-specific checks");
            Console.WriteLine();
            Console.WriteLine("  This model is language-agnostic — prefer it (codebase.Types, Type.Name,");
            Console.WriteLine("  Type.Kind, Type.Modifiers, Type.BaseTypes, ...) so checks work across languages.");
            Console.WriteLine();
            Console.WriteLine("  For a fact the common model can't express, narrow a Type to a language-specific");
            Console.WriteLine("  subtype, then read its extra fields:");
            Console.WriteLine();
            Console.WriteLine("    :asCSharp -> CSharpType    :asRust -> RustType      :asJava -> JavaType");
            Console.WriteLine("    :asPython -> PythonType    :asGo -> GoType          :asJavaScript -> JavaScriptType");
            Console.WriteLine();
            Console.WriteLine("  The same :asXxx also narrows Methods and Statements (control blocks, error");
            Console.WriteLine("  handling, ...) to e.g. CSharpMethod / CSharpStatement — narrow the matching");
            Console.WriteLine("  collection the same way:");
            Console.WriteLine();
            Console.WriteLine("    codebase.Methods:asCSharp:<predicate>     codebase.Statements:asRust:<predicate>");
            Console.WriteLine();
            Console.WriteLine("  Cop functions, predicates, and commands use codebase.Methods:asCop -> CopFunction.");
            Console.WriteLine();
            Console.WriteLine("  Run 'cop help <language>' (e.g. cop help csharp) to see each subtype's fields.");
            Console.WriteLine();
        }

        return 0;
    }
    /// Input format: "  PropName : TypeName" or "  PropName : TypeName?"
    /// </summary>
    private static void WritePropertyLine(string prop)
    {
        bool color = ConsoleMarkdown.UseColor;
        int colonIdx = prop.IndexOf(" : ", StringComparison.Ordinal);
        if (colonIdx < 0 || !color)
        {
            Console.WriteLine(prop);
            return;
        }

        string namePart = prop[..colonIdx];
        string typePart = prop[(colonIdx + 3)..];
        Console.Write(namePart);
        ConsoleMarkdown.WriteTypeAnnotation(typePart);
        Console.WriteLine();
    }

    /// <summary>
    /// Writes a function signature with keyword "function" colored.
    /// Input format: "function name(params) : ReturnType"
    /// </summary>
    private static void WriteColoredSignature(string sig)
    {
        bool color = ConsoleMarkdown.UseColor;
        if (!color || !sig.StartsWith("function "))
        {
            Console.Write(sig);
            return;
        }

        // Color the "function" keyword
        Console.Write($"{ConsoleMarkdown.Cyan}function{ConsoleMarkdown.Reset} ");

        string rest = sig["function ".Length..];

        // Find the return type annotation
        int colonIdx = rest.LastIndexOf(") : ", StringComparison.Ordinal);
        if (colonIdx >= 0)
        {
            string nameAndParams = rest[..(colonIdx + 1)];
            string returnType = rest[(colonIdx + 4)..];
            Console.Write($"{ConsoleMarkdown.Bold}{nameAndParams}{ConsoleMarkdown.Reset}");
            ConsoleMarkdown.WriteTypeAnnotation(returnType);
        }
        else
        {
            Console.Write($"{ConsoleMarkdown.Bold}{rest}{ConsoleMarkdown.Reset}");
        }
    }

    private static string FormatFunctionSignature(FunctionDecl fd)
    {
        IEnumerable<string> paramParts = fd.Params.Select(p =>
        {
            if (p.Type != null)
            {
                return $"{p.Name}: {FormatTypeRef(p.Type, false)}";
            }

            return p.Name;
        });
        string returnType = fd.ReturnType != null ? $" : {FormatTypeRef(fd.ReturnType, false)}" : "";
        return $"function {fd.Name}({string.Join(", ", paramParts)}){returnType}";
    }

    private static string FormatTypeRef(TypeRef tr, bool isOptional)
    {
        string name = tr.IsCollection ? $"[{tr.Name}]" : tr.Name;
        return isOptional ? $"{name}?" : name;
    }

    private static readonly HashSet<string> s_copKeywords = new(StringComparer.Ordinal)
    {
        "import", "export", "let", "type", "enum", "flags", "predicate", "function",
        "command", "foreach", "async", "if", "else", "match", "true", "false", "nic"
    };

    /// <summary>
    /// Writes cop source code with syntax highlighting:
    /// keywords in cyan, comments in gray, strings in green.
    /// </summary>
    private static void WriteCopSource(string source)
    {
        bool color = ConsoleMarkdown.UseColor;
        string[] lines = source.Split('\n');

        foreach (string rawLine in lines)
        {
            string line = rawLine.TrimEnd('\r');

            if (!color)
            {
                Console.WriteLine($"  {line}");
                continue;
            }

            // Comment lines
            if (line.TrimStart().StartsWith('#'))
            {
                Console.WriteLine($"  {ConsoleMarkdown.Gray}{line}{ConsoleMarkdown.Reset}");
                continue;
            }

            // Colorize the line token by token
            Console.Write("  ");
            WriteCopLine(line);
            Console.WriteLine();
        }
    }

    private static string? ExtractFrontmatterField(string content, string field)
    {
        if (!content.StartsWith("---"))
        {
            return null;
        }

        int endIdx = content.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (endIdx < 0)
        {
            return null;
        }

        string frontmatter = content[3..endIdx];
        foreach (string line in frontmatter.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith($"{field}:", StringComparison.OrdinalIgnoreCase))
            {
                string value = trimmed[($"{field}:".Length)..].Trim();
                return value.Length > 0 ? value : null;
            }
        }
        return null;
    }

    private static void WriteCopLine(string line)
    {
        int i = 0;
        while (i < line.Length)
        {
            // Inline comment
            if (line[i] == '#')
            {
                Console.Write($"{ConsoleMarkdown.Gray}{line[i..]}{ConsoleMarkdown.Reset}");
                return;
            }

            // String literal (single quotes)
            if (line[i] == '\'')
            {
                int end = line.IndexOf('\'', i + 1);
                if (end < 0)
                {
                    end = line.Length - 1;
                }

                string str = line[i..(end + 1)];
                Console.Write($"{ConsoleMarkdown.Green}{str}{ConsoleMarkdown.Reset}");
                i = end + 1;
                continue;
            }

            // Identifier or keyword
            if (char.IsLetter(line[i]) || line[i] == '_')
            {
                int start = i;
                while (i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] == '_'))
                {
                    i++;
                }

                string word = line[start..i];
                if (s_copKeywords.Contains(word))
                {
                    Console.Write($"{ConsoleMarkdown.Cyan}{word}{ConsoleMarkdown.Reset}");
                }
                else
                {
                    Console.Write(word);
                }

                continue;
            }

            Console.Write(line[i]);
            i++;
        }
    }
}
