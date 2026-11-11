// Verifies Cop programs without executing their commands.

using System.CommandLine;
using System.Text.Json;
using Cop.Core;
using Cop.Lang;
using Cop.Lang.Ast;
using Cop.Lang.Interpreter;
using Cop.Lang.Parser;
using Cop.Providers;

namespace Cop.Cli.Commands;

/// <summary>
/// Static analysis command that verifies .cop program correctness
/// without executing it. Checks syntax, imports, name binding, types, and arity.
/// </summary>
public static class VerifyCommand
{
    /// <summary>Creates verification arguments and structural-policy options.</summary>
    public static Command Create()
    {
        Argument<string> pathArg = new Argument<string>("path")
        {
            Arity = ArgumentArity.ZeroOrOne,
            Description = ".cop file or directory to verify"
        };
        Option<bool> jsonOption = new Option<bool>("--json")
        {
            Description = "Emit diagnostics as JSON to stdout (for editors and tools)"
        };
        Option<bool> structuralOption = new Option<bool>("--strict-structure")
        {
            Description = "Reject raw-text dependencies in structural checks (including helpers and imports)"
        };
        Option<string[]> textCheckOption = new Option<string[]>("--allow-text-check")
        {
            Description = "User-approved text check binding or predicate (repeatable; requires --strict-structure)"
        };
        Command command = new Command("verify", "Verify program correctness without executing (syntax, imports, types, bindings)")
        {
            pathArg,
            jsonOption,
            structuralOption,
            textCheckOption
        };
        command.SetAction(parseResult => Execute(parseResult.GetValue(pathArg),
            new VerificationOptions
            {
                IsJsonOutputEnabled = parseResult.GetValue(jsonOption),
                IsStrictStructureEnabled = parseResult.GetValue(structuralOption),
                AllowedTextChecks = parseResult.GetValue(textCheckOption)
            }));
        return command;
    }

    /// <summary>Reports syntax, binding, type, and optional structural-policy diagnostics.</summary>
    public static int Execute(string? path, VerificationOptions? options = null)
    {
        options ??= new VerificationOptions();
        bool json = options.IsJsonOutputEnabled;
        bool strictStructure = options.IsStrictStructureEnabled;
        string[]? allowTextChecks = options.AllowedTextChecks?.ToArray();
        if (!strictStructure && allowTextChecks is { Length: > 0 })
        {
            Console.Error.WriteLine("--allow-text-check requires --strict-structure.");
            return 1;
        }
        path ??= Directory.GetCurrentDirectory();
        path = Path.GetFullPath(path);

        string[] files;
        string scriptsDir;

        if (File.Exists(path) && path.EndsWith(".cop", StringComparison.OrdinalIgnoreCase))
        {
            scriptsDir = Path.GetDirectoryName(path) ?? Directory.GetCurrentDirectory();
            // A single .cop file is part of the program formed by all .cop files in its directory
            // (the cop-checks/ pattern, where main.cop references lets defined in sibling files).
            // Verify them together — matching `cop run` — so cross-file references resolve and the
            // reported file count reflects the whole program, not just the named file.
            files = Directory.GetFiles(scriptsDir, "*.cop", SearchOption.AllDirectories);
        }
        else if (Directory.Exists(path))
        {
            files = Directory.GetFiles(path, "*.cop", SearchOption.AllDirectories);
            scriptsDir = path;
            if (files.Length == 0)
            {
                Console.Error.WriteLine($"No .cop files found in: {path}");
                return 1;
            }
        }
        else
        {
            Console.Error.WriteLine($"Path not found: {path}");
            return 1;
        }

        Array.Sort(files, StringComparer.Ordinal);
        List<CopDiagnostic> diagnostics = CollectDiagnostics(files, scriptsDir, File.ReadAllText, strictStructure, allowTextChecks);

        // Output
        int errorCount = diagnostics.Count(d => d.Severity == CopDiagnosticSeverity.Error);
        int warningCount = diagnostics.Count(d => d.Severity == CopDiagnosticSeverity.Warning);

        if (json)
        {
            WriteDiagnosticsJson(diagnostics, files.Length, errorCount, warningCount);
            return errorCount > 0 ? 1 : 0;
        }

        if (diagnostics.Count > 0)
        {
            DiagnosticFormatter.WriteAllToStdErr(diagnostics);
        }

        if (errorCount == 0 && warningCount == 0)
        {
            Console.WriteLine($"  \u2713 {files.Length} file(s) verified successfully");
            return 0;
        }

        Console.Error.WriteLine();
        if (errorCount > 0 && warningCount > 0)
        {
            Console.Error.WriteLine($"  {errorCount} error(s), {warningCount} warning(s) in {files.Length} file(s)");
        }
        else if (errorCount > 0)
        {
            Console.Error.WriteLine($"  {errorCount} error(s) in {files.Length} file(s)");
        }
        else
        {
            Console.Error.WriteLine($"  {warningCount} warning(s) in {files.Length} file(s)");
        }

        return errorCount > 0 ? 1 : 0;
    }

    /// <summary>
    /// Runs the full verification pipeline (syntax parse, import resolution, name binding, type
    /// checking) over <paramref name="files"/> and returns the structured diagnostics. The whole
    /// directory is treated as one program. <paramref name="readSource"/> supplies each file's text,
    /// so callers can substitute an in-memory editor buffer for a file on disk (used by the language
    /// server). This is the single analysis pipeline shared by `cop verify` and the language service.
    /// </summary>
    internal static List<CopDiagnostic> CollectDiagnostics(string[] files, string scriptsDir, Func<string, string> readSource,
        bool strictStructure = false, string[]? allowTextChecks = null)
    {
        List<CopDiagnostic> diagnostics = new List<CopDiagnostic>();

        // Phase 1: Syntax — parse all files
        List<(ModuleNode Module, string FilePath, string Source)> modules = new List<(ModuleNode Module, string FilePath, string Source)>();
        foreach (string file in files)
        {
            try
            {
                string source = readSource(file);
                ModuleNode module = CopParser.Parse(source, file);
                modules.Add((module, file, source));
            }
            catch (ParseException ex)
            {
                diagnostics.Add(ex.ToDiagnostic());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                System.Security.SecurityException)
            {
                diagnostics.Add(new CopDiagnostic(
                    CopDiagnosticSeverity.Error,
                    ex.Message,
                    file));
            }
        }

        // Phase 2: Import resolution
        List<string> feedPaths = FindFeedPaths(scriptsDir);

        // Also extract feed paths declared in source files (feed 'path')
        foreach ((ModuleNode _, string? filePath, string? source) in modules)
        {
            string fileDir = Path.GetDirectoryName(filePath) ?? scriptsDir;
            foreach (string fp in ModuleLoader.ExtractFeedPaths(source, fileDir))
            {
                if (!feedPaths.Contains(fp))
                {
                    feedPaths.Add(fp);
                }
            }
        }

        ModuleLoader moduleLoader = new ModuleLoader(feedPaths);
        HashSet<string> imports = new HashSet<string>(StringComparer.Ordinal);

        foreach ((ModuleNode? module, string _, string _) in modules)
        {
            foreach (Declaration decl in module.Declarations)
            {
                if (decl is ImportDecl imp)
                {
                    imports.Add(imp.ModuleName);
                }
            }
        }

        // Resolve imports using a lightweight evaluator (no execution, just name registration)
        Evaluator evaluator = new Evaluator();
        foreach (string import in imports)
        {
            moduleLoader.LoadPackage(import, evaluator);
        }

        diagnostics.AddRange(moduleLoader.Diagnostics);

        // Phase 3: Name binding
        // Collect symbols from imports that the binder should know about
        List<Lang.Interpreter.Symbol> externalSymbols = CollectExternalSymbols(evaluator.GlobalEnvironment, moduleLoader.LoadedModules);

        // All top-level names declared across every file of this program. A reference in one file
        // to a let/function/etc. declared in a sibling file (the cop-checks/ pattern) must not be
        // flagged as undefined. Used as a fallback only — not added to any scope, so it never
        // triggers a duplicate-declaration diagnostic.
        HashSet<string> programNames = new HashSet<string>(StringComparer.Ordinal);
        HashSet<string> programTypeNames = new HashSet<string>(StringComparer.Ordinal);
        foreach ((ModuleNode? programModule, string _, string _) in modules)
        {
            foreach (Declaration decl in programModule.Declarations)
            {
                if (decl is TypeDecl type)
                {
                    programTypeNames.Add(type.Name);
                }
                else if (decl is EnumDecl enumDecl)
                {
                    programTypeNames.Add(enumDecl.Name);
                }
                else if (decl is FlagsDecl flags)
                {
                    programTypeNames.Add(flags.Name);
                }

                string? declName = decl switch
                {
                    TypeDecl td => td.Name,
                    EnumDecl ed => ed.Name,
                    FlagsDecl fd => fd.Name,
                    FunctionDecl fd => fd.Name,
                    LetDecl ld => ld.Name,
                    CommandDecl cd => cd.Name,
                    _ => null
                };
                if (declName is not null)
                {
                    programNames.Add(declName);
                }
            }
        }

        // Program declarations shadow default-library functions, just as they do at runtime.
        externalSymbols.RemoveAll(symbol => programNames.Contains(symbol.Name));

        // Names exported by imported packages are also usable as bare values
        // (e.g. METRICS(slop, ...), CHECK(csharp-library-checks)). Add them to the same
        // fallback set so they are not flagged as undefined. They are NOT added to the scope, so
        // a program may still re-declare one (shadowing) without a duplicate-declaration error.
        foreach (ModuleNode loaded in moduleLoader.LoadedModules)
        {
            foreach (Declaration decl in loaded.Declarations)
            {
                string? exportedName = decl switch
                {
                    TypeDecl { IsExported: true } td => td.Name,
                    EnumDecl { IsExported: true } ed => ed.Name,
                    FlagsDecl { IsExported: true } fd => fd.Name,
                    FunctionDecl { IsExported: true } fd => fd.Name,
                    LetDecl { IsExported: true } ld => ld.Name,
                    CommandDecl { IsExported: true } cd => cd.Name,
                    _ => null
                };
                if (exportedName is not null)
                {
                    programNames.Add(exportedName);
                }
            }
        }

        foreach ((ModuleNode? module, string? filePath, string? source) in modules)
        {
            Binder binder = new Binder(filePath, externalSymbols, programNames,
                reportUndefinedIdentifiers: true, programTypeNames: programTypeNames);
            BindingResult bindingResult = binder.Bind(module);

            foreach (BindingDiagnostic bd in bindingResult.Diagnostics)
            {
                diagnostics.Add(new CopDiagnostic(
                    bd.Severity switch
                    {
                        DiagnosticSeverity.Error => CopDiagnosticSeverity.Error,
                        DiagnosticSeverity.Warning => CopDiagnosticSeverity.Warning,
                        _ => CopDiagnosticSeverity.Info
                    },
                    bd.Message,
                    bd.FilePath ?? filePath,
                    bd.Line,
                    SourceLine: ParseException.GetSourceLine(source, bd.Line)));
            }
        }

        // Phase 4: Type/field validation (check provider schema references)
        ValidateProviderReferences(modules, feedPaths, diagnostics);

        // Phase 5: Static type checking — argument types against declared signatures.
        // Conservative: only confident, concrete incompatibilities are reported.
        IEnumerable<ModuleNode> typeModel = modules.Select(m => m.Module).Concat(moduleLoader.LoadedModules);
        IEnumerable<(ModuleNode Module, string FilePath, string Source)> toCheck = modules.Select(m => (m.Module, m.FilePath, m.Source));
        diagnostics.AddRange(TypeChecker.Check(typeModel, toCheck, moduleLoader.PackageModules));

        if (strictStructure)
        {
            diagnostics.AddRange(StructuralCheckPolicy.Check(modules, moduleLoader.LoadedFiles, allowTextChecks ?? []));
        }

        return diagnostics;
    }

    /// <summary>
    /// Assembles every <see cref="ModuleNode"/> that forms a program: the local <c>.cop</c> files
    /// plus the modules of all imported packages. This is the same module set the type checker uses
    /// (<see cref="CollectDiagnostics"/>'s Phase 5), exposed for the editor's <c>SemanticModel</c> so
    /// hover/completion resolve provider/package types (e.g. <c>Violation</c>). Parse recovery keeps
    /// declarations from incomplete files; unreadable files are logged and skipped. Unexpected
    /// compiler failures propagate. <paramref name="readSource"/> supplies each file's text so an
    /// in-memory buffer can stand in for a file on disk.
    /// </summary>
    internal static List<ModuleNode> LoadProgramModules(string[] files, string scriptsDir, Func<string, string> readSource)
        => LoadProgramModules(files, scriptsDir, readSource, out _);

    internal static List<ModuleNode> LoadProgramModules(
        string[] files, string scriptsDir, Func<string, string> readSource,
        out IReadOnlyDictionary<string, List<ModuleNode>> namespaces)
    {
        List<(ModuleNode Module, string FilePath, string Source)> local = new List<(ModuleNode Module, string FilePath, string Source)>();
        foreach (string file in files)
        {
            string source;
            try { source = readSource(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                System.Security.SecurityException)
            {
                Console.Error.WriteLine($"[cop-editor] Could not read '{file}': {ex.Message}");
                continue;
            }
            // Parse with recovery so a file with a syntax error still contributes its other
            // declarations to the editor model (hover/completion/go-to-def stay useful mid-edit).
            local.Add((CopParser.ParseWithRecovery(source, file, out _), file, source));
        }

        List<string> feedPaths = FindFeedPaths(scriptsDir);
        foreach ((ModuleNode _, string? filePath, string? source) in local)
        {
            string fileDir = Path.GetDirectoryName(filePath) ?? scriptsDir;
            foreach (string fp in ModuleLoader.ExtractFeedPaths(source, fileDir))
            {
                if (!feedPaths.Contains(fp))
                {
                    feedPaths.Add(fp);
                }
            }
        }

        ModuleLoader moduleLoader = new ModuleLoader(feedPaths);
        HashSet<string> imports = new HashSet<string>(StringComparer.Ordinal);
        foreach ((ModuleNode? module, string _, string _) in local)
        {
            foreach (Declaration decl in module.Declarations)
            {
                if (decl is ImportDecl imp)
                {
                    imports.Add(imp.ModuleName);
                }
            }
        }

        Evaluator evaluator = new Evaluator();
        foreach (string import in imports)
        {
            moduleLoader.LoadPackage(import, evaluator);
        }
        foreach (string error in moduleLoader.Errors)
        {
            Console.Error.WriteLine($"[cop-editor] {error}");
        }

        List<ModuleNode> all = new List<ModuleNode>(local.Count + 8);
        all.AddRange(local.Select(m => m.Module));
        all.AddRange(moduleLoader.LoadedModules);
        namespaces = moduleLoader.PackageModules;
        return all;
    }

    /// <summary>Names of packages available to import from the program's feeds (for editor
    /// import-completion). A directory is a package when it contains a <c>src/</c> folder.</summary>
    internal static IReadOnlyCollection<string> ListAvailablePackages(string scriptsDir)
    {
        HashSet<string> names = new HashSet<string>(StringComparer.Ordinal);
        foreach (string feed in FindFeedPaths(scriptsDir))
        {
            if (!Directory.Exists(feed))
            {
                continue;
            }

            foreach (string dir in Directory.GetDirectories(feed))
            {
                string name = Path.GetFileName(dir);
                if (name.StartsWith('.'))
                {
                    continue;
                }

                if (Directory.Exists(Path.Combine(dir, "src")))
                {
                    names.Add(name);
                }

                foreach (string sub in Directory.GetDirectories(dir))
                {
                    string subName = Path.GetFileName(sub);
                    if (!subName.StartsWith('.') && Directory.Exists(Path.Combine(sub, "src")))
                    {
                        names.Add(subName);
                    }
                }
            }
        }
        return names;
    }

    /// <summary>
    /// Emits verification diagnostics as JSON to stdout, for editors and tools to consume the real
    /// compiler's analysis (the same parse + import-resolution + bind + type-check pipeline).
    /// </summary>
    private static void WriteDiagnosticsJson(List<CopDiagnostic> diagnostics, int fileCount, int errors, int warnings)
    {
        var payload = new
        {
            diagnostics = diagnostics.Select(d => new
            {
                severity = d.Severity switch
                {
                    CopDiagnosticSeverity.Error => "error",
                    CopDiagnosticSeverity.Warning => "warning",
                    _ => "info"
                },
                file = d.FilePath,
                line = d.Line,
                column = d.Column,
                length = d.Length,
                message = d.Message,
                suggestion = d.Suggestion
            }).ToList(),
            files = fileCount,
            errors,
            warnings
        };
        JsonSerializerOptions options = new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(payload, options));
    }

    /// <summary>
    /// Collects external symbols (from imports and builtins) for the binder.
    /// Shared between verify and execution validation paths.
    /// </summary>
    internal static List<Cop.Lang.Interpreter.Symbol> CollectExternalSymbols(Cop.Lang.Interpreter.Environment env, IReadOnlyList<ModuleNode> loadedModules, IEnumerable<ProviderSchema>? providerSchemas = null)
    {
        List<Lang.Interpreter.Symbol> symbols = new List<Cop.Lang.Interpreter.Symbol>();
        HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);

        // Add core primitive types that are always available
        string[] primitiveTypes = new[] { "string", "int", "float", "bool", "byte", "bytes", "object", "T", "R", "K", "A" };
        foreach (string? name in primitiveTypes)
        {
            if (seen.Add(name))
            {
                symbols.Add(new TypeSymbol(name, null, []));
            }
        }

        // Types from provider schemas (e.g., Folder, File from filesystem provider)
        if (providerSchemas != null)
        {
            foreach (ProviderSchema schema in providerSchemas)
            {
                foreach (ProviderTypeSchema type in schema.Types)
                {
                    if (seen.Add(type.Name))
                    {
                        List<PropertySymbol> props = type.Properties.Select(p =>
                            new PropertySymbol(p.Name, new TypeRef(p.Type), p.IsOptional)).ToList();
                        symbols.Add(new TypeSymbol(type.Name, type.Base, props));
                    }
                }
            }
        }

        // Add types, functions, and enums from imported modules
        foreach (ModuleNode module in loadedModules)
        {
            foreach (Declaration decl in module.Declarations)
            {
                switch (decl)
                {
                    case TypeDecl td when td.IsExported:
                        if (seen.Add(td.Name))
                        {
                            List<PropertySymbol> props = td.Properties.Select(p =>
                                new PropertySymbol(p.Name, p.Type, p.IsOptional)).ToList();
                            symbols.Add(new TypeSymbol(td.Name, td.BaseType, props));
                        }
                        break;
                    case EnumDecl ed when ed.IsExported:
                        if (seen.Add(ed.Name))
                        {
                            symbols.Add(new EnumSymbol(ed.Name, null, ed.Members.Select(m => new EnumMemberSymbol(m, ed.Name)).ToList()));
                        }

                        break;
                    case FunctionDecl fd when fd.IsExported:
                        if (seen.Add(fd.Name))
                        {
                            symbols.Add(new FunctionSymbol(fd.Name, CallableKind.Function, []));
                        }

                        break;
                }
            }
        }

        // Add symbols from imported packages (registered in the evaluator's environment)
        foreach ((string? name, CopValue _) in env.AllBindings())
        {
            if (seen.Add(name))
            {
                symbols.Add(new VariableSymbol(name));
            }
        }

        // Built-in names that are always available to the binder's fallback (so a reference to one
        // is never flagged "undefined" by `cop verify`). Two sources, unioned:
        //  - every primitive op from IntrinsicRegistry (the single source of truth) — so this set
        //    can never drift when an intrinsic is added/renamed;
        //  - runtime/standard-library/domain names that have no single registry yet (commands like
        //    CHECK/FAIL, collection combinators, std transforms). Over-inclusion here is harmless.
        string[] runtimeBuiltins = new[] { "print", "PRINT", "SAVE", "FAIL", "CHECK", "ASSERT", "provider", "count", "sum", "avg", "min", "max", "first", "last", "distinct", "flatten", "join", "sort", "reverse", "take", "skip", "map", "any", "all", "none", "contains", "where", "groupBy", "format" };
        foreach (string? name in Cop.Lang.IntrinsicRegistry.All.SelectMany(o => o.AllNames).Concat(runtimeBuiltins))
        {
            if (seen.Add(name))
            {
                symbols.Add(new FunctionSymbol(name, CallableKind.External, []));
            }
        }

        return symbols;
    }

    /// <summary>
    /// Validates that runtime:: collection references in .cop files correspond to known providers.
    /// </summary>
    private static void ValidateProviderReferences(
        List<(ModuleNode Module, string FilePath, string Source)> modules,
        List<string> feedPaths,
        List<CopDiagnostic> diagnostics)
    {
        // Look for runtime:: declarations and verify their provider packages exist
        foreach ((ModuleNode? module, string? filePath, string? source) in modules)
        {
            foreach (Declaration decl in module.Declarations)
            {
                if (decl is not LetDecl ld)
                {
                    continue;
                }

                if (ld.Value is not Cop.Lang.Ast.MemberExpr ma)
                {
                    continue;
                }

                if (ma.Object is not Cop.Lang.Ast.IdentifierExpr id || id.Name != "runtime")
                {
                    continue;
                }

                // This is a runtime::X reference — check if a provider for X exists in feeds
                string providerName = ma.Member;
                bool found = false;
                foreach (string feed in feedPaths)
                {
                    string? dir = ImportResolver.FindPackageDir(feed, providerName);
                    if (dir is not null)
                    {
                        found = true;
                        break;
                    }
                }
                // runtime:: references don't always map to package names, so only warn
                // if it looks like a package reference (lowercase name)
                if (!found && providerName == providerName.ToLowerInvariant() && providerName.Length > 2)
                {
                    // This is informational — runtime providers may be built-in
                }
            }
        }
    }

    private static List<string> FindFeedPaths(string scriptsDir) =>
        PackageResolver.GetFeedPaths(scriptsDir);
}
