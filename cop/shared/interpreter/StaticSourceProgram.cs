namespace Cop.Lang.Interpreter;

using Cop.Lang.Ast;
using Cop.Lang.Parser;

public sealed record StaticSourceFile(ModuleNode Module, string Path, string Source);
public sealed record StaticDeclarationLocation(string Path, string? Namespace, int Line);

/// <summary>Loads local source declarations and imports without constructing an evaluator.</summary>
public sealed class StaticSourceProgram
{
    public StaticSemanticModel Model { get; }
    public IReadOnlyList<string> Diagnostics { get; }
    private readonly Dictionary<FunctionDecl, StaticDeclarationLocation> _locations = new(ReferenceEqualityComparer.Instance);

    public StaticDeclarationLocation? LocationOf(FunctionDecl function) => _locations.GetValueOrDefault(function);

    public StaticSourceProgram(IEnumerable<StaticSourceFile> files, IEnumerable<string> feedPaths)
    {
        var local = files.ToList();
        var modules = local.Select(f => f.Module).ToList();
        var diagnostics = new List<string>();
        var feeds = feedPaths.ToList();
        foreach (var file in local)
        {
            feeds.AddRange(ModuleLoader.ExtractFeedPaths(file.Source, System.IO.Path.GetDirectoryName(file.Path)!));
            foreach (var function in file.Module.Declarations.OfType<FunctionDecl>())
                _locations[function] = new(file.Path, null, function.Line);
        }
        var imported = new HashSet<string>(StringComparer.Ordinal);
        void Import(string name)
        {
            if (!imported.Add(name)) return;
            string? directory = null;
            try
            {
                foreach (var feed in feeds.Distinct(StringComparer.Ordinal))
                    if ((directory = ImportResolver.FindPackageDir(feed, name)) is not null) break;
                if (directory is null)
                {
                    diagnostics.Add($"Import '{name}' is unavailable to declaration-only analysis; restore it to a local source feed.");
                    return;
                }
                var sourceDirectory = System.IO.Path.Combine(directory, "src");
                if (!Directory.Exists(sourceDirectory))
                {
                    diagnostics.Add($"Import '{name}' has no source declarations in src/.");
                    return;
                }
                var exports = new List<Declaration>();
                foreach (var path in Directory.GetFiles(sourceDirectory, "*.cop").Order(StringComparer.Ordinal))
                {
                    var text = File.ReadAllText(path);
                    var module = CopParser.Parse(text, path);
                    foreach (var dependency in module.Declarations.OfType<ImportDecl>()) Import(dependency.ModuleName);
                    foreach (var declaration in module.Declarations)
                    {
                        switch (declaration)
                        {
                            case FunctionDecl { IsExported: true } function:
                                // An imported body has its own private scope. Its declared signature is
                                // visible here, but inferring that body in the caller's scope is unsound.
                                var signature = function with { Body = new IntrinsicBody() };
                                exports.Add(signature);
                                _locations[signature] = new(path, name, function.Line);
                                var qualified = signature with { Name = $"{name}.{function.Name}" };
                                exports.Add(qualified);
                                _locations[qualified] = new(path, name, function.Line);
                                break;
                            case TypeDecl { IsExported: true }:
                            case EnumDecl { IsExported: true }:
                            case FlagsDecl { IsExported: true }:
                                exports.Add(declaration);
                                break;
                            case LetDecl { IsExported: true } let:
                                // Keep explicit metadata, not dependency-sensitive value execution.
                                exports.Add(let with { Value = new IdentifierExpr("__static_unavailable_value", let.Line) });
                                break;
                        }
                    }
                }
                modules.Add(new(exports));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ParseException)
            {
                diagnostics.Add($"Import '{name}' source analysis failed: {ex.Message}");
            }
        }
        foreach (var import in local.SelectMany(f => f.Module.Declarations.OfType<ImportDecl>()))
            Import(import.ModuleName);
        Model = new(modules);
        Diagnostics = diagnostics;
    }
}
