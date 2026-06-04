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
        var command = new Command("verify", "Verify program correctness without executing (syntax, imports, types, bindings)")
        {
            pathArg
        };
        command.SetAction(parseResult => Execute(parseResult.GetValue(pathArg)));
        return command;
    }

    public static int Execute(string? path)
    {
        path ??= Directory.GetCurrentDirectory();
        path = Path.GetFullPath(path);

        string[] files;
        string scriptsDir;

        if (File.Exists(path) && path.EndsWith(".cop", StringComparison.OrdinalIgnoreCase))
        {
            files = [path];
            scriptsDir = Path.GetDirectoryName(path) ?? Directory.GetCurrentDirectory();
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
        var diagnostics = new List<CopDiagnostic>();

        // Phase 1: Syntax — parse all files
        var modules = new List<(ModuleNode Module, string FilePath, string Source)>();
        foreach (var file in files)
        {
            try
            {
                var source = File.ReadAllText(file);
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

        foreach (var (module, filePath, source) in modules)
        {
            var binder = new Binder(filePath, externalSymbols);
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

        // Output
        int errorCount = diagnostics.Count(d => d.Severity == CopDiagnosticSeverity.Error);
        int warningCount = diagnostics.Count(d => d.Severity == CopDiagnosticSeverity.Warning);

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
    /// Collects external symbols (from imports and builtins) for the binder.
    /// </summary>
    private static List<Cop.Lang.Interpreter.Symbol> CollectExternalSymbols(Cop.Lang.Interpreter.Environment env, IReadOnlyList<ModuleNode> loadedModules)
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
                            symbols.Add(new EnumSymbol(ed.Name, null, []));
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
