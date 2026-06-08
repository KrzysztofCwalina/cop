namespace Cop.Lang.Interpreter;

using Cop.Lang.Ast;
using Cop.Lang.Parser;

/// <summary>
/// Loads .cop modules (packages) via the new parser and registers them in the evaluator.
/// Handles import resolution, transitive dependencies, and export visibility.
/// This replaces the old ImportResolver + TypeRegistry import loading.
/// </summary>
public sealed class ModuleLoader
{
    private readonly string[] _feedPaths;
    private readonly HashSet<string> _loadedPackages = new(StringComparer.Ordinal);
    private readonly List<string> _errors = [];
    private readonly List<CopDiagnostic> _diagnostics = [];
    private readonly List<(string Dir, string PackageName)> _providerPackages = [];
    private readonly List<(LetDecl Decl, string FilePath, Environment ModuleEnv)> _deferredLetBindings = [];
    private readonly List<ModuleNode> _loadedModules = [];

    public IReadOnlyList<string> Errors => _errors;
    public IReadOnlyList<CopDiagnostic> Diagnostics => _diagnostics;
    public IReadOnlyList<(string Dir, string PackageName)> ProviderPackages => _providerPackages;
    public IReadOnlyList<ModuleNode> LoadedModules => _loadedModules;

    public ModuleLoader(IEnumerable<string> feedPaths)
    {
        _feedPaths = feedPaths.ToArray();
    }

    /// <summary>
    /// Load a package by name: parse its .cop files and register exported declarations in the evaluator.
    /// Handles transitive imports.
    /// </summary>
    public void LoadPackage(string packageName, Evaluator evaluator)
    {
        if (!_loadedPackages.Add(packageName))
            return; // already loaded

        var packageDir = FindPackageDir(packageName);
        if (packageDir is null)
        {
            // Try to suggest a close package name from available feeds
            var available = GetAvailablePackageNames();
            var suggestion = StringDistance.FindClosest(packageName, available);
            _errors.Add($"Import '{packageName}' could not be resolved");
            _diagnostics.Add(new CopDiagnostic(
                CopDiagnosticSeverity.Error,
                $"Import '{packageName}' could not be resolved. Package not found in any feed.",
                Suggestion: suggestion));
            return;
        }

        var srcDir = Path.Combine(packageDir, "src");
        if (!Directory.Exists(srcDir))
        {
            _errors.Add($"Package '{packageName}' has no src/ directory");
            _diagnostics.Add(new CopDiagnostic(
                CopDiagnosticSeverity.Error,
                $"Package '{packageName}' has no src/ directory"));
            return;
        }

        // Check if this is a provider package
        DetectProvider(packageDir, packageName);

        var copFiles = Directory.GetFiles(srcDir, "*.cop");
        Array.Sort(copFiles, StringComparer.Ordinal);

        var modules = new List<(ModuleNode Module, string Path)>();
        var allImports = new List<string>();

        foreach (var file in copFiles)
        {
            try
            {
                var source = File.ReadAllText(file);
                var module = CopParser.Parse(source, file);
                modules.Add((module, file));

                // Collect imports for transitive resolution
                foreach (var decl in module.Declarations)
                {
                    if (decl is ImportDecl imp)
                        allImports.Add(imp.ModuleName);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _errors.Add($"{file}: {ex.Message}");
            }
        }

        // Resolve transitive imports first (so dependencies are available)
        foreach (var import in allImports)
            LoadPackage(import, evaluator);

        // Register exported declarations in the evaluator
        foreach (var (module, path) in modules)
        {
            _loadedModules.Add(module);
            RegisterExportedDeclarations(module, path, evaluator);
        }
    }

    /// <summary>
    /// Resolve all imports found in already-loaded modules.
    /// Call this after loading user script files to resolve their import statements.
    /// </summary>
    public void ResolveImports(IEnumerable<ModuleNode> userModules, Evaluator evaluator)
    {
        var imports = new HashSet<string>(StringComparer.Ordinal);

        foreach (var module in userModules)
        {
            foreach (var decl in module.Declarations)
            {
                if (decl is ImportDecl imp)
                    imports.Add(imp.ModuleName);
            }
        }

        foreach (var import in imports)
            LoadPackage(import, evaluator);
    }

    /// <summary>
    /// Extract feed declarations from modules (feed 'path' syntax).
    /// </summary>
    public static List<string> ExtractFeedPaths(string source, string scriptsDir)
    {
        var paths = new List<string>();
        // Simple line scan for feed declarations (feed 'path' or feed "path")
        foreach (var line in source.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("feed ", StringComparison.Ordinal))
                continue;

            var rest = trimmed[5..].Trim();
            if (rest.Length >= 2 && (rest[0] == '\'' || rest[0] == '"'))
            {
                var quote = rest[0];
                var end = rest.IndexOf(quote, 1);
                if (end > 0)
                {
                    var feedPath = rest[1..end];
                    var resolved = Path.IsPathRooted(feedPath)
                        ? Path.GetFullPath(feedPath)
                        : Path.GetFullPath(Path.Combine(scriptsDir, feedPath));
                    if (Directory.Exists(resolved))
                        paths.Add(resolved);
                }
            }
        }
        return paths;
    }

    private void RegisterExportedDeclarations(ModuleNode module, string filePath, Evaluator evaluator)
    {
        // Each module gets its own scope for non-exported let bindings.
        // This prevents name collisions (e.g., multiple packages defining 'let cb = ...').
        var moduleEnv = evaluator.GlobalEnvironment.Extend();

        foreach (var decl in module.Declarations)
        {
            switch (decl)
            {
                case FunctionDecl fd:
                    // ALL functions/predicates go into moduleEnv (for use by module-internal let bindings).
                    // Exported ones are ALSO registered in globalEnv for external access.
                    var func = new CopFunction(fd, moduleEnv);
                    moduleEnv.Define(fd.Name, func);
                    if (fd.IsExported)
                        RegisterFunctionWithOverloading(evaluator.GlobalEnvironment, fd.Name, func);
                    break;

                case LetDecl ld:
                    // Defer all let bindings (they may depend on provider data).
                    // Track which module env they belong to.
                    _deferredLetBindings.Add((ld, filePath, moduleEnv));
                    break;

                case CommandDecl cd when cd.IsExported:
                    var cmdCallable = new CopCommandFunction(cd, moduleEnv);
                    evaluator.GlobalEnvironment.Define(cd.Name, cmdCallable);
                    break;

                case EnumDecl ed when ed.IsExported:
                    foreach (var member in ed.Members)
                        evaluator.GlobalEnvironment.Define(member, new CopString(member));
                    break;

                case FlagsDecl fd2 when fd2.IsExported:
                    for (int i = 0; i < fd2.Members.Count; i++)
                        evaluator.GlobalEnvironment.Define(fd2.Members[i], new CopInt(1 << i));
                    break;
            }
        }
    }

    /// <summary>
    /// Evaluate all deferred let bindings from imported packages.
    /// Call AFTER provider data has been registered.
    /// Uses lazy thunks to handle cross-file references regardless of file order.
    /// Non-exported lets go only in their module scope; exported lets also go in global.
    /// </summary>
    public void EvalDeferredLetBindings(Evaluator evaluator, List<string> errors)
    {
        foreach (var (ld, filePath, moduleEnv) in _deferredLetBindings)
        {
            try
            {
                var capturedLd = ld;
                var capturedModuleEnv = moduleEnv;
                var thunk = new CopThunk(() => evaluator.Eval(capturedLd.Value, capturedModuleEnv));

                // Always register in the module env (for module-local access)
                moduleEnv.Define(ld.Name, thunk);

                // Exported lets are also visible globally
                if (ld.IsExported)
                    evaluator.GlobalEnvironment.Define(ld.Name, thunk);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                errors.Add($"{filePath}: {ex.Message}");
            }
        }
    }

    private string? FindPackageDir(string packageName)
    {
        foreach (var feedPath in _feedPaths)
        {
            var dir = ImportResolver.FindPackageDir(feedPath, packageName);
            if (dir is not null) return dir;
        }
        return null;
    }

    private void DetectProvider(string packageDir, string packageName)
    {
        // Check for provider metadata (cop.json with provider field, or .md manifest)
        var metaFile = Path.Combine(packageDir, "cop.json");
        if (File.Exists(metaFile))
        {
            try
            {
                var json = File.ReadAllText(metaFile);
                if (json.Contains("\"provider\"", StringComparison.OrdinalIgnoreCase))
                    _providerPackages.Add((packageDir, packageName));
            }
            catch { }
        }
    }

    /// <summary>
    /// Register a function, creating a function group if there's already a function with the same name.
    /// </summary>
    private static void RegisterFunctionWithOverloading(Environment env, string name, CopFunction func)
    {
        if (env.TryLookup(name, out var existing))
        {
            if (existing is CopFunctionGroup group)
            {
                group.Add(func);
                return;
            }
            if (existing is CopFunction existingFunc)
            {
                var newGroup = new CopFunctionGroup(name);
                newGroup.Add(existingFunc);
                newGroup.Add(func);
                env.Define(name, newGroup);
                return;
            }
        }
        env.Define(name, func);
    }

    /// <summary>
    /// Lists all available package names from configured feed paths.
    /// Used for "Did you mean?" suggestions on import failures.
    /// </summary>
    private List<string> GetAvailablePackageNames()
    {
        var names = new List<string>();
        foreach (var feedPath in _feedPaths)
        {
            if (!Directory.Exists(feedPath)) continue;
            foreach (var dir in Directory.GetDirectories(feedPath))
            {
                var name = Path.GetFileName(dir);
                if (!name.StartsWith('.'))
                    names.Add(name);
                // Also check subdirectories (e.g., dotnet/, js/, python/ under packages/)
                foreach (var subDir in Directory.GetDirectories(dir))
                {
                    var subName = Path.GetFileName(subDir);
                    if (!subName.StartsWith('.') && Directory.Exists(Path.Combine(subDir, "src")))
                        names.Add(subName);
                }
            }
        }
        return names;
    }
}
