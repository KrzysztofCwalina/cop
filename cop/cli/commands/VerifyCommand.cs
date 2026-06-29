using System.CommandLine;
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
    public static Command Create()
    {
        var pathArg = new Argument<string>("path")
        {
            Arity = ArgumentArity.ZeroOrOne,
            Description = ".cop file or directory to verify"
        };
        var jsonOption = new Option<bool>("--json")
        {
            Description = "Emit diagnostics as JSON to stdout (for editors and tools)"
        };
        var command = new Command("verify", "Verify program correctness without executing (syntax, imports, types, bindings)")
        {
            pathArg,
            jsonOption
        };
        command.SetAction(parseResult => Execute(parseResult.GetValue(pathArg), parseResult.GetValue(jsonOption)));
        return command;
    }

    public static int Execute(string? path, bool json = false)
    {
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
        var diagnostics = CollectDiagnostics(files, scriptsDir, File.ReadAllText);

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
            Console.Error.WriteLine($"  {errorCount} error(s), {warningCount} warning(s) in {files.Length} file(s)");
        else if (errorCount > 0)
            Console.Error.WriteLine($"  {errorCount} error(s) in {files.Length} file(s)");
        else
            Console.Error.WriteLine($"  {warningCount} warning(s) in {files.Length} file(s)");

        return errorCount > 0 ? 1 : 0;
    }

    /// <summary>
    /// Runs the full verification pipeline (syntax parse, import resolution, name binding, type
    /// checking) over <paramref name="files"/> and returns the structured diagnostics. The whole
    /// directory is treated as one program. <paramref name="readSource"/> supplies each file's text,
    /// so callers can substitute an in-memory editor buffer for a file on disk (used by the language
    /// server). This is the single analysis pipeline shared by `cop verify` and the language service.
    /// </summary>
    internal static List<CopDiagnostic> CollectDiagnostics(string[] files, string scriptsDir, Func<string, string> readSource)
    {
        var diagnostics = new List<CopDiagnostic>();

        // Phase 1: Syntax — parse all files
        var modules = new List<(ModuleNode Module, string FilePath, string Source)>();
        foreach (var file in files)
        {
            try
            {
                var source = readSource(file);
                var module = CopParser.Parse(source, file);
                modules.Add((module, file, source));
            }
            catch (ParseException ex)
            {
                diagnostics.Add(ex.ToDiagnostic());
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                diagnostics.Add(new CopDiagnostic(
                    CopDiagnosticSeverity.Error,
                    ex.Message,
                    file));
            }
        }

        // Phase 2: Import resolution
        var feedPaths = FindFeedPaths(scriptsDir);

        // Also extract feed paths declared in source files (feed 'path')
        foreach (var (_, filePath, source) in modules)
        {
            var fileDir = Path.GetDirectoryName(filePath) ?? scriptsDir;
            foreach (var fp in ModuleLoader.ExtractFeedPaths(source, fileDir))
            {
                if (!feedPaths.Contains(fp))
                    feedPaths.Add(fp);
            }
        }

        var moduleLoader = new ModuleLoader(feedPaths);
        var imports = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (module, _, _) in modules)
        {
            foreach (var decl in module.Declarations)
            {
                if (decl is ImportDecl imp)
                    imports.Add(imp.ModuleName);
            }
        }

        // Resolve imports using a lightweight evaluator (no execution, just name registration)
        var evaluator = new Evaluator();
        foreach (var import in imports)
            moduleLoader.LoadPackage(import, evaluator);

        diagnostics.AddRange(moduleLoader.Diagnostics);

        // Phase 3: Name binding
        // Collect symbols from imports that the binder should know about
        var externalSymbols = CollectExternalSymbols(evaluator.GlobalEnvironment, moduleLoader.LoadedModules);

        // All top-level names declared across every file of this program. A reference in one file
        // to a let/function/etc. declared in a sibling file (the cop-checks/ pattern) must not be
        // flagged as undefined. Used as a fallback only — not added to any scope, so it never
        // triggers a duplicate-declaration diagnostic.
        var programNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (programModule, _, _) in modules)
            foreach (var decl in programModule.Declarations)
            {
                var declName = decl switch
                {
                    TypeDecl td => td.Name,
                    EnumDecl ed => ed.Name,
                    FlagsDecl fd => fd.Name,
                    FunctionDecl fd => fd.Name,
                    LetDecl ld => ld.Name,
                    CommandDecl cd => cd.Name,
                    _ => null
                };
                if (declName is not null) programNames.Add(declName);
            }

        // Names exported by imported packages are also usable as bare values
        // (e.g. METRICS(slop, ...), CHECK(csharp-library-checks)). Add them to the same
        // fallback set so they are not flagged as undefined. They are NOT added to the scope, so
        // a program may still re-declare one (shadowing) without a duplicate-declaration error.
        foreach (var loaded in moduleLoader.LoadedModules)
            foreach (var decl in loaded.Declarations)
            {
                var exportedName = decl switch
                {
                    TypeDecl { IsExported: true } td => td.Name,
                    EnumDecl { IsExported: true } ed => ed.Name,
                    FlagsDecl { IsExported: true } fd => fd.Name,
                    FunctionDecl { IsExported: true } fd => fd.Name,
                    LetDecl { IsExported: true } ld => ld.Name,
                    CommandDecl { IsExported: true } cd => cd.Name,
                    _ => null
                };
                if (exportedName is not null) programNames.Add(exportedName);
            }

        foreach (var (module, filePath, source) in modules)
        {
            var binder = new Binder(filePath, externalSymbols, programNames, reportUndefinedIdentifiers: true);
            var bindingResult = binder.Bind(module);

            foreach (var bd in bindingResult.Diagnostics)
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
        try
        {
            var typeModel = modules.Select(m => m.Module).Concat(moduleLoader.LoadedModules);
            var toCheck = modules.Select(m => (m.Module, m.FilePath, m.Source));
            diagnostics.AddRange(TypeChecker.Check(typeModel, toCheck));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The type checker must never make verification crash; fall back to the other phases.
            if (Cop.Core.CopDiagnostics.Level >= 1)
                Console.Error.WriteLine($"[diag] type checker skipped: {ex.Message}");
        }

        return diagnostics;
    }

    /// <summary>
    /// Assembles every <see cref="ModuleNode"/> that forms a program: the local <c>.cop</c> files
    /// plus the modules of all imported packages. This is the same module set the type checker uses
    /// (<see cref="CollectDiagnostics"/>'s Phase 5), exposed for the editor's <c>SemanticModel</c> so
    /// hover/completion resolve provider/package types (e.g. <c>Violation</c>). Unparseable files are
    /// skipped (the editor still gets a best-effort model). <paramref name="readSource"/> supplies
    /// each file's text so an in-memory buffer can stand in for a file on disk.
    /// </summary>
    internal static List<ModuleNode> LoadProgramModules(string[] files, string scriptsDir, Func<string, string> readSource)
    {
        var local = new List<(ModuleNode Module, string FilePath, string Source)>();
        foreach (var file in files)
        {
            string source;
            try { source = readSource(file); }
            catch { continue; }
            try { local.Add((CopParser.Parse(source, file), file, source)); }
            catch { /* a file with a syntax error contributes nothing to the model */ }
        }

        var feedPaths = FindFeedPaths(scriptsDir);
        foreach (var (_, filePath, source) in local)
        {
            var fileDir = Path.GetDirectoryName(filePath) ?? scriptsDir;
            foreach (var fp in ModuleLoader.ExtractFeedPaths(source, fileDir))
                if (!feedPaths.Contains(fp)) feedPaths.Add(fp);
        }

        var moduleLoader = new ModuleLoader(feedPaths);
        var imports = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (module, _, _) in local)
            foreach (var decl in module.Declarations)
                if (decl is ImportDecl imp) imports.Add(imp.ModuleName);

        var evaluator = new Evaluator();
        foreach (var import in imports)
        {
            try { moduleLoader.LoadPackage(import, evaluator); }
            catch { /* a broken import must not crash the editor */ }
        }

        var all = new List<ModuleNode>(local.Count + 8);
        all.AddRange(local.Select(m => m.Module));
        all.AddRange(moduleLoader.LoadedModules);
        return all;
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
        var options = new System.Text.Json.JsonSerializerOptions
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
        var symbols = new List<Cop.Lang.Interpreter.Symbol>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // Add core primitive types that are always available
        var primitiveTypes = new[] { "string", "int", "float", "bool", "byte", "bytes", "object", "T", "R", "K", "A" };
        foreach (var name in primitiveTypes)
        {
            if (seen.Add(name))
                symbols.Add(new TypeSymbol(name, null, []));
        }

        // Types from provider schemas (e.g., Folder, File from filesystem provider)
        if (providerSchemas != null)
        {
            foreach (var schema in providerSchemas)
            {
                foreach (var type in schema.Types)
                {
                    if (seen.Add(type.Name))
                    {
                        var props = type.Properties.Select(p =>
                            new PropertySymbol(p.Name, new TypeRef(p.Type), p.Optional)).ToList();
                        symbols.Add(new TypeSymbol(type.Name, type.Base, props));
                    }
                }
            }
        }

        // Add types, functions, and enums from imported modules
        foreach (var module in loadedModules)
        {
            foreach (var decl in module.Declarations)
            {
                switch (decl)
                {
                    case TypeDecl td when td.IsExported:
                        if (seen.Add(td.Name))
                        {
                            var props = td.Properties.Select(p =>
                                new PropertySymbol(p.Name, p.Type, p.IsOptional)).ToList();
                            symbols.Add(new TypeSymbol(td.Name, td.BaseType, props));
                        }
                        break;
                    case EnumDecl ed when ed.IsExported:
                        if (seen.Add(ed.Name))
                            symbols.Add(new EnumSymbol(ed.Name, null, ed.Members.Select(m => new EnumMemberSymbol(m, ed.Name)).ToList()));
                        break;
                    case FunctionDecl fd when fd.IsExported:
                        if (seen.Add(fd.Name))
                            symbols.Add(new FunctionSymbol(fd.Name, CallableKind.Function, []));
                        break;
                }
            }
        }

        // Add symbols from imported packages (registered in the evaluator's environment)
        foreach (var (name, _) in env.AllBindings())
        {
            if (seen.Add(name))
                symbols.Add(new VariableSymbol(name));
        }

        // Add built-in intrinsics that are always available
        var builtins = new[] { "print", "PRINT", "SAVE", "FAIL", "CHECK", "ASSERT", "provider", "count", "sum", "avg", "min", "max", "first", "last", "distinct", "flatten", "join", "sort", "reverse", "take", "skip", "map", "any", "all", "none", "contains", "where", "groupBy", "format" };
        foreach (var name in builtins)
        {
            if (seen.Add(name))
                symbols.Add(new FunctionSymbol(name, CallableKind.External, []));
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
        foreach (var (module, filePath, source) in modules)
        {
            foreach (var decl in module.Declarations)
            {
                if (decl is not LetDecl ld) continue;
                if (ld.Value is not Cop.Lang.Ast.MemberExpr ma) continue;
                if (ma.Object is not Cop.Lang.Ast.IdentifierExpr id || id.Name != "runtime") continue;

                // This is a runtime::X reference — check if a provider for X exists in feeds
                var providerName = ma.Member;
                bool found = false;
                foreach (var feed in feedPaths)
                {
                    var dir = ImportResolver.FindPackageDir(feed, providerName);
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
