using Cop.Core;
using Cop.Providers.SourceModel;
using Cop.Providers.SourceParsers;

namespace Cop.Providers;

/// <summary>Execution-scoped source snapshot. Discovery lists paths; each file is read and parsed on demand.</summary>
public sealed class DeferredSourceSnapshot
{
    private readonly string _root;
    private readonly SourceParserRegistry _parsers;
    private readonly Lazy<IReadOnlyList<string>> _paths;
    private readonly Dictionary<string, Lazy<SourceFile?>> _files = new(StringComparer.Ordinal);
    private readonly Func<string, SourceFile?> _parse;
    private readonly Lazy<List<ProjectInfo>>? _projects;
    private readonly Lazy<List<(string Name, string Directory, HashSet<string>? Files)>>? _projectDirectories;
    private readonly DeferredSourceCache? _cache;
    private readonly Dictionary<(string Path, string Collection), List<object>> _collections = new();

    public IReadOnlyList<string> Paths => _paths.Value;

    public DeferredSourceSnapshot(SourceParserRegistry parsers, ProviderQuery query,
        Func<string, SourceFile?>? parse = null, Func<List<ProjectInfo>>? discoverProjects = null)
    {
        _parsers = parsers;
        _root = Path.GetFullPath(query.RootPath!);
        _paths = new(() =>
        {
            var paths = new List<string>();
            CodeCollectionBuilder.CollectSourceFiles(_root, parsers, query.ExcludedDirectories, paths);
            paths.Sort(StringComparer.Ordinal);
            return paths;
        });
        _parse = parse ?? (path => ParseFile(parsers, path));
        if (parse is null && OperatingSystem.IsWindows())
            _cache = new(_root + "\n" + parsers.CacheAssembly.ManifestModule.ModuleVersionId + "\n" +
                string.Join("\n", (query.ExcludedDirectories ?? new HashSet<string>()).Order(StringComparer.Ordinal)));
        _projects = discoverProjects is null ? null : new(discoverProjects);
        if (_projects is not null)
            _projectDirectories = new(() => _projects.Value.Select(project =>
            {
                var directory = Path.GetDirectoryName(Path.GetFullPath(project.Path, _root))!;
                var files = project.SourceFiles is null ? null : new HashSet<string>(
                    project.SourceFiles.Select(path => Path.GetFullPath(path, directory)),
                    OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
                return (project.Name, Path.EndsInDirectorySeparator(directory)
                    ? directory : directory + Path.DirectorySeparatorChar, files);
            }).ToList());
    }

    private SourceFile? ParseFile(SourceParserRegistry parsers, string path)
    {
        var parser = parsers.GetParser(Path.GetExtension(path))!;
        if (_cache is not null)
        {
            var source = _cache.Read(path, parser);
            if (_files.Count == Paths.Count)
                _cache.Save(Paths);
            return source;
        }
        var info = new FileInfo(path);
        var fingerprint = System.Text.Encoding.UTF8.GetBytes(
            $"{info.Length}:{info.LastWriteTimeUtc.Ticks}:{parser.GetCacheIdentity(path)}");
        var canCache = parser.CanCache(path);
        var cachePath = CodeCollectionBuilder.GetCachePath(_root + "\nfile\n" + path + "\n"
            + parser.GetType().Assembly.ManifestModule.ModuleVersionId);
        var cached = canCache ? SourceCacheSerializer.TryLoad(cachePath, fingerprint, parser.GetType().Assembly) : null;
        if (cached is { Count: 1 })
        {
            if (CopDiagnostics.Timing)
                Console.Error.WriteLine($"[provider-cache] {cached[0].Language} {Path.GetRelativePath(_root, path)}");
            return cached[0];
        }
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        var parsed = parser.Parse(path, reader.ReadToEnd());
        if (parsed is null) return null;
        if (CopDiagnostics.Timing)
            Console.Error.WriteLine($"[provider-parse] {parsed.Language} {Path.GetRelativePath(_root, path)}");
        if (canCache && parsed.ParseErrors.Count == 0)
        {
            try { SourceCacheSerializer.Save(cachePath, fingerprint, [parsed]); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"Warning: Could not cache '{path}': {ex.Message}");
            }
        }
        return parsed;
    }

    private SourceFile? ReadFile(string path)
    {
        if (!_files.TryGetValue(path, out var file))
        {
            file = new(() =>
            {
                var parsed = _parse(path);
                if (parsed is null) return null;
                var normalized = parsed with
                {
                    Path = Path.GetRelativePath(_root, path).Replace('\\', '/'),
                    FullPath = Path.GetFullPath(path),
                    SourceRoot = _root
                };
                CodeCollectionBuilder.LinkReferences(normalized);
                void DeferMembers(TypeDeclaration type)
                {
                    type.DeferMembers(() => ResolveMembers(type, normalized));
                    foreach (var nested in type.NestedTypes) DeferMembers(nested);
                }
                if (_parsers.GetParser(Path.GetExtension(path))!.SupportsCrossFileMembers)
                    foreach (var type in normalized.Types) DeferMembers(type);
                if (_projectDirectories is not null)
                    normalized.DeferProjects(() => _projectDirectories.Value
                        .Where(project => project.Files is not null
                            ? project.Files.Contains(normalized.FullPath)
                            : normalized.FullPath.StartsWith(project.Directory,
                                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                        .Select(project => project.Name).ToList());
                foreach (var error in normalized.ParseErrors)
                    ProviderErrors.Report(error);
                if (CopDiagnostics.Timing)
                    Console.Error.WriteLine($"[provider-model] {normalized.Language} {normalized.Path}");
                return normalized;
            });
            _files.Add(path, file);
        }
        return file.Value;
    }

    private List<MethodDeclaration> ResolveMembers(TypeDeclaration type, SourceFile owner)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var directory = Path.GetDirectoryName(owner.FullPath);
        var extension = Path.GetExtension(owner.FullPath);
        var files = Paths.Where(path => comparer.Equals(Path.GetDirectoryName(path), directory)
                && comparer.Equals(Path.GetExtension(path), extension))
            .Select(ReadFile).OfType<SourceFile>()
            .Where(file => file.Language == owner.Language).ToList();
        return CodeCollectionBuilder.ResolveReceiverMembers(type, owner, files);
    }

    public DeferredProviderResult Query(ProviderQuery query, RuntimeBindings? bindings = null)
    {
        var collections = new Dictionary<string, IEnumerable<object>>(StringComparer.Ordinal);
        var extractors = CodeBindings.BuildExtractors();
        var accessors = bindings?.Accessors ?? CodeBindings.BuildAccessors();
        var itemTypes = CodeCollectionBuilder.GetCollectionItemTypes();
        foreach (var (name, extractor) in extractors)
        {
            if (query.Collection is not null && query.Collection != name) continue;
            Func<object, bool>? predicate = null;
            var filter = query.CollectionFilters?.GetValueOrDefault(name) ?? query.Filter;
            if (filter is not null && itemTypes.TryGetValue(name, out var type)
                && accessors.TryGetValue(type, out var fields))
                predicate = Cop.Lang.FilterCompiler.Compile(filter, fields);
            collections[name] = Enumerate(name, extractor, predicate);
        }
        if (_projects is not null && (query.Collection is null or "Projects"))
        {
            var filter = query.CollectionFilters?.GetValueOrDefault("Projects") ?? query.Filter;
            var predicate = filter is null ? null : Cop.Lang.FilterCompiler.Compile(filter, accessors["Project"]);
            collections["Projects"] = Projects(predicate);
        }
        return new(collections);
    }

    private IEnumerable<object> Enumerate(string collection, Func<object, List<object>> extract, Func<object, bool>? predicate)
    {
        foreach (var path in Paths)
        {
            var file = ReadFile(path);
            if (file is null) continue;
            IEnumerable<object> items;
            if (collection == "Types")
                items = file.Types;
            else if (collection == "Statements")
                items = file.Statements;
            else if (collection == "Calls")
                items = file.Statements.Where(static statement => statement.Kind == "call");
            else
            {
                var key = (path, collection);
                if (!_collections.TryGetValue(key, out var projected))
                {
                    projected = extract(file);
                    _collections.Add(key, projected);
                }
                items = projected;
            }
            foreach (var item in items)
                if (predicate is null || predicate(item))
                    yield return item;
        }
    }

    private IEnumerable<object> Projects(Func<object, bool>? predicate)
    {
        foreach (var project in _projects!.Value)
            if (predicate is null || predicate(project))
                yield return project;
    }
}
