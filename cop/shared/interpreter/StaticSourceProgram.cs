// Combines parsed source modules for static semantic analysis.

namespace Cop.Lang.Interpreter;

using Cop.Lang.Ast;
using Cop.Lang.Parser;

/// <summary>Loads local source declarations and imports without constructing an evaluator.</summary>
public sealed class StaticSourceProgram
{
    /// <summary>The semantic model built from local and imported declarations.</summary>
    public StaticSemanticModel Model { get; }

    /// <summary>Problems encountered while resolving source files and imports.</summary>
    public IReadOnlyList<string> Diagnostics { get; }
    private readonly Dictionary<FunctionDecl, StaticDeclarationLocation> _locations = new(ReferenceEqualityComparer.Instance);

    /// <summary>Finds the source location recorded for a resolved function.</summary>
    public StaticDeclarationLocation? LocationOf(FunctionDecl function) => _locations.GetValueOrDefault(function);

    /// <summary>
    /// Loads source imports and builds a static model without evaluation.
    /// </summary>
    public StaticSourceProgram(IEnumerable<StaticSourceFile> files, IEnumerable<string> feedPaths)
    {
        List<StaticSourceFile> local = files.ToList();
        List<ModuleNode> modules = local.Select(f => f.Module).ToList();
        List<string> diagnostics = new List<string>();
        List<string> feeds = feedPaths.ToList();
        foreach (StaticSourceFile? file in local)
        {
            foreach (string feed in file.Module.FeedPaths)
            {
                if (Uri.TryCreate(feed, UriKind.Absolute, out Uri? uri) && (!uri.IsFile || uri.IsUnc))
                {
                    continue;
                }

                try
                {
                    string path = uri is { IsFile: true } ? uri.LocalPath
                        : System.IO.Path.GetFullPath(feed, System.IO.Path.GetDirectoryName(file.Path)!);
                    if (Directory.Exists(path))
                    {
                        feeds.Add(path);
                    }
                }
                catch (ArgumentException ex)
                {
                    diagnostics.Add($"Feed in '{file.Path}' cannot be used for local source analysis: {ex.Message}");
                }
            }
            foreach (FunctionDecl function in file.Module.Declarations.OfType<FunctionDecl>())
            {
                _locations[function] = new(file.Path, null, function.Line);
            }
        }
        HashSet<string> directImports = local.SelectMany(file => file.Module.Declarations.OfType<ImportDecl>())
            .Select(import => import.ModuleName).ToHashSet(StringComparer.Ordinal);
        HashSet<string> imported = new HashSet<string>(StringComparer.Ordinal);
        int sourceFiles = 0;
        long sourceBytes = 0;
        void Import(string name, int depth = 0)
        {
            if (!imported.Add(name))
            {
                return;
            }

            if (depth >= 64 || imported.Count > 256)
            {
                diagnostics.Add($"Import '{name}' exceeds the declaration-only analysis limit (64 levels / 256 packages).");
                return;
            }
            string? directory = null;
            try
            {
                foreach (string? feed in feeds.Distinct(StringComparer.Ordinal))
                {
                    if ((directory = ImportResolver.FindPackageDir(feed, name)) is not null)
                    {
                        break;
                    }
                }

                if (directory is null)
                {
                    diagnostics.Add($"Import '{name}' is unavailable to declaration-only analysis; restore it to a local source feed.");
                    return;
                }
                string sourceDirectory = System.IO.Path.Combine(directory, "src");
                if (!Directory.Exists(sourceDirectory))
                {
                    diagnostics.Add($"Import '{name}' has no source declarations in src/.");
                    return;
                }
                List<Declaration> exports = new List<Declaration>();
                List<string> paths = Directory.EnumerateFiles(sourceDirectory, "*.cop").Take(4097 - Math.Min(sourceFiles, 4096))
                    .Order(StringComparer.Ordinal).ToList();
                if (paths.Count == 0)
                {
                    diagnostics.Add($"Import '{name}' has no .cop source declarations.");
                }

                foreach (string path in paths)
                {
                    if (++sourceFiles > 4096)
                    {
                        diagnostics.Add("Imported declaration analysis exceeded its 4096-file limit.");
                        return;
                    }
                    FileInfo info = new FileInfo(path);
                    if (info.Length > 32 * 1024 * 1024 - sourceBytes)
                    {
                        diagnostics.Add($"Import '{name}' exceeds the 32 MiB source-text analysis limit.");
                        return;
                    }
                    string text = File.ReadAllText(path);
                    sourceBytes += System.Text.Encoding.UTF8.GetByteCount(text);
                    if (sourceBytes > 32 * 1024 * 1024)
                    {
                        diagnostics.Add($"Import '{name}' exceeds the 32 MiB source-text analysis limit.");
                        return;
                    }
                    ModuleNode module = CopParser.Parse(text, path);
                    foreach (ImportDecl dependency in module.Declarations.OfType<ImportDecl>())
                    {
                        Import(dependency.ModuleName, depth + 1);
                    }

                    foreach (Declaration declaration in module.Declarations)
                    {
                        switch (declaration)
                        {
                            case FunctionDecl { IsExported: true } function:
                                // An imported body has its own private scope. Its declared signature is
                                // visible here, but inferring that body in the caller's scope is unsound.
                                FunctionDecl signature = function with { Body = new IntrinsicBody() };
                                exports.Add(signature);
                                _locations[signature] = new(path, name, function.Line);
                                FunctionDecl qualified = signature with { Name = $"{name}.{function.Name}" };
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
                if (directImports.Contains(name))
                {
                    modules.Add(new(exports));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ParseException)
            {
                diagnostics.Add($"Import '{name}' source analysis failed: {ex.Message}");
            }
        }
        foreach (string import in directImports)
        {
            Import(import);
        }

        Model = new(modules);
        Diagnostics = diagnostics;
    }
}
