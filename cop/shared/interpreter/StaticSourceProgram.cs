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
            foreach (var feed in file.Module.FeedPaths)
            {
                if (Uri.TryCreate(feed, UriKind.Absolute, out var uri) && (!uri.IsFile || uri.IsUnc)) continue;
                try
                {
                    var path = uri is { IsFile: true } ? uri.LocalPath
                        : System.IO.Path.GetFullPath(feed, System.IO.Path.GetDirectoryName(file.Path)!);
                    if (Directory.Exists(path)) feeds.Add(path);
                }
                catch (ArgumentException ex)
                {
                    diagnostics.Add($"Feed in '{file.Path}' cannot be used for local source analysis: {ex.Message}");
                }
            }
            foreach (var function in file.Module.Declarations.OfType<FunctionDecl>())
                _locations[function] = new(file.Path, null, function.Line);
        }
        var directImports = local.SelectMany(file => file.Module.Declarations.OfType<ImportDecl>())
            .Select(import => import.ModuleName).ToHashSet(StringComparer.Ordinal);
        var imported = new HashSet<string>(StringComparer.Ordinal);
        int sourceFiles = 0;
        long sourceBytes = 0;
        void Import(string name, int depth = 0)
        {
            if (!imported.Add(name)) return;
            if (depth >= 64 || imported.Count > 256)
            {
                diagnostics.Add($"Import '{name}' exceeds the declaration-only analysis limit (64 levels / 256 packages).");
                return;
            }
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
                var paths = Directory.EnumerateFiles(sourceDirectory, "*.cop").Take(4097 - Math.Min(sourceFiles, 4096))
                    .Order(StringComparer.Ordinal).ToList();
                if (paths.Count == 0)
                    diagnostics.Add($"Import '{name}' has no .cop source declarations.");
                foreach (var path in paths)
                {
                    if (++sourceFiles > 4096)
                    {
                        diagnostics.Add("Imported declaration analysis exceeded its 4096-file limit.");
                        return;
                    }
                    var info = new FileInfo(path);
                    if (info.Length > 32 * 1024 * 1024 - sourceBytes)
                    {
                        diagnostics.Add($"Import '{name}' exceeds the 32 MiB source-text analysis limit.");
                        return;
                    }
                    var text = File.ReadAllText(path);
                    sourceBytes += System.Text.Encoding.UTF8.GetByteCount(text);
                    if (sourceBytes > 32 * 1024 * 1024)
                    {
                        diagnostics.Add($"Import '{name}' exceeds the 32 MiB source-text analysis limit.");
                        return;
                    }
                    var module = CopParser.Parse(text, path);
                    foreach (var dependency in module.Declarations.OfType<ImportDecl>()) Import(dependency.ModuleName, depth + 1);
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
                if (directImports.Contains(name)) modules.Add(new(exports));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ParseException)
            {
                diagnostics.Add($"Import '{name}' source analysis failed: {ex.Message}");
            }
        }
        foreach (var import in directImports) Import(import);
        Model = new(modules);
        Diagnostics = diagnostics;
    }
}
