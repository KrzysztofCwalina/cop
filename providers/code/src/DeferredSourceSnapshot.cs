// Discovers source inputs and parses only the files demanded by queries.

using Cop.Core;
using Cop.Providers.SourceModel;
using Cop.Providers.SourceParsers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace Cop.Providers;

/// <summary>Execution-scoped source snapshot. Discovery lists paths; each file is read and parsed on demand.</summary>
public sealed class DeferredSourceSnapshot
{
    private readonly string _root;
    private readonly SourceParserRegistry _parsers;
    private readonly Lazy<IReadOnlyList<string>> _paths;
    private readonly Dictionary<string, AsyncProviderValue<SourceFile?>> _files = new(StringComparer.Ordinal);
    private readonly Func<string, CancellationToken, ValueTask<SourceFile?>> _parse;
    private readonly AsyncProviderValue<List<ProjectInfo>>? _projects;
    private readonly DeferredSourceCache? _cache;
    private int _parsedFiles;
    private readonly Dictionary<(string Path, string Collection), List<object>> _collections = new();

    /// <summary>
    /// The source paths captured by this execution snapshot.
    /// </summary>
    public IReadOnlyList<string> Paths => _paths.Value;

    /// <summary>
    /// Captures parser assignments and source paths for deferred queries.
    /// </summary>
    public DeferredSourceSnapshot(SourceParserRegistry parsers, ProviderQuery query,
        Func<string, CancellationToken, ValueTask<SourceFile?>>? parse = null,
        Func<CancellationToken, ValueTask<List<ProjectInfo>>>? discoverProjects = null)
    {
        _parsers = parsers;
        _root = Path.GetFullPath(query.RootPath!);
        _paths = new(() =>
        {
            List<string> paths = new List<string>();
            CodeCollectionBuilder.CollectSourceFiles(_root, parsers, query.ExcludedDirectories, paths);
            paths.Sort(StringComparer.Ordinal);
            return paths;
        });
        _parse = parse ?? ((path, token) => ParseFileAsync(parsers, path, token));
        if (parse is null && OperatingSystem.IsWindows())
        {
            _cache = new(_root + "\n" + parsers.CacheAssembly.ManifestModule.ModuleVersionId + "\n" +
                string.Join("\n", (query.ExcludedDirectories ?? new HashSet<string>()).Order(StringComparer.Ordinal)));
        }

        _projects = discoverProjects is null ? null : new(discoverProjects);
    }

    private async ValueTask<SourceFile?> ParseFileAsync(
        SourceParserRegistry parsers, string path, CancellationToken cancellationToken)
    {
        ISourceParser parser = parsers.GetParser(Path.GetExtension(path))!;
        if (_cache is not null)
        {
            SourceFile? source = await _cache.ReadAsync(path, parser, cancellationToken)
                .ConfigureAwait(false);
            return source;
        }
        using FileStream stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using StreamReader reader = new StreamReader(stream);
        string text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        byte[] fingerprint = SHA256.HashData(Encoding.UTF8.GetBytes(
            parser.GetCacheIdentity(path) + "\n" + text));
        bool canCache = parser.CanCache(path);
        string cachePath = CodeCollectionBuilder.GetCachePath(_root + "\nfile\n" + path + "\n"
            + parser.GetType().Assembly.ManifestModule.ModuleVersionId);
        List<SourceFile>? cached = canCache ? SourceCacheSerializer.LoadOrDefault(cachePath, fingerprint, parser.GetType().Assembly) : null;
        if (cached is { Count: 1 })
        {
            if (CopDiagnostics.IsTimingEnabled)
            {
                Console.Error.WriteLine($"[provider-cache] {cached[0].Language} {Path.GetRelativePath(_root, path)}");
            }

            return cached[0];
        }
        SourceFile? parsed = await parser.ParseAsync(path, text, cancellationToken)
            .ConfigureAwait(false);
        if (parsed is null)
        {
            return null;
        }

        if (CopDiagnostics.IsTimingEnabled)
        {
            Console.Error.WriteLine($"[provider-parse] {parsed.Language} {Path.GetRelativePath(_root, path)}");
        }

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

    private async ValueTask<SourceFile?> ReadFileAsync(
        string path, CancellationToken cancellationToken)
    {
        AsyncProviderValue<SourceFile?> file;
        lock (_files)
        {
            if (!_files.TryGetValue(path, out file!))
            {
                file = new(token => NormalizeFileAsync(path, token));
                _files.Add(path, file);
            }
        }
        SourceFile? result = await file.GetValueAsync(cancellationToken).ConfigureAwait(false);
        if (result is not null)
        {
            ExecutionStatistics.RecordInputFile(path);
        }

        return result;
    }

    private async ValueTask<SourceFile?> NormalizeFileAsync(
        string path, CancellationToken cancellationToken)
    {
                SourceFile? parsed = await _parse(path, cancellationToken).ConfigureAwait(false);
                if (Interlocked.Increment(ref _parsedFiles) == Paths.Count)
                {
                    _cache?.Save(Paths);
                }
                if (parsed is null)
                {
                    return null;
                }

                SourceFile normalized = parsed with
                {
                    Path = Path.GetRelativePath(_root, path).Replace('\\', '/'),
                    FullPath = Path.GetFullPath(path),
                    SourceRoot = _root
                };
                CodeCollectionBuilder.LinkReferences(normalized);
                void DeferMembers(TypeDeclaration type)
                {
                    type.DeferMembersAsync(token => ResolveMembersAsync(type, normalized, token));
                    foreach (TypeDeclaration nested in type.NestedTypes)
                    {
                        DeferMembers(nested);
                    }
                }
                if (_parsers.GetParser(Path.GetExtension(path))!.SupportsCrossFileMembers)
                {
                    foreach (TypeDeclaration type in normalized.Types)
                    {
                        DeferMembers(type);
                    }
                }

                if (_projects is not null)
                {
                    normalized.DeferProjectsAsync(async token =>
                    {
                        List<ProjectInfo> projects = await _projects.GetValueAsync(token)
                            .ConfigureAwait(false);
                        return projects.Where(project => ContainsFile(project, normalized))
                            .Select(project => project.Name).ToList();
                    });
                }

                foreach (string error in normalized.ParseErrors)
                {
                    ProviderErrors.Report(error);
                }

                if (CopDiagnostics.IsTimingEnabled)
                {
                    Console.Error.WriteLine($"[provider-model] {normalized.Language} {normalized.Path}");
                }

                return normalized;
    }

    private bool ContainsFile(ProjectInfo project, SourceFile file)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(project.Path, _root))!;
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return project.SourceFiles is not null
            ? project.SourceFiles.Any(path =>
                string.Equals(Path.GetFullPath(path, directory), file.FullPath, comparison))
            : file.FullPath.StartsWith(Path.EndsInDirectorySeparator(directory)
                ? directory : directory + Path.DirectorySeparatorChar, comparison);
    }

    private async ValueTask<List<MethodDeclaration>> ResolveMembersAsync(
        TypeDeclaration type, SourceFile owner, CancellationToken cancellationToken)
    {
        StringComparer comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        string? directory = Path.GetDirectoryName(owner.FullPath);
        string extension = Path.GetExtension(owner.FullPath);
        List<SourceFile> files = [];
        foreach (string path in Paths.Where(path =>
                     comparer.Equals(Path.GetDirectoryName(path), directory)
                     && comparer.Equals(Path.GetExtension(path), extension)))
        {
            SourceFile? file = await ReadFileAsync(path, cancellationToken).ConfigureAwait(false);
            if (file is not null && file.Language == owner.Language)
            {
                files.Add(file);
            }
        }
        return CodeCollectionBuilder.ResolveReceiverMembers(type, owner, files);
    }

    /// <summary>
    /// Enumerates the requested source collection, parsing only demanded files.
    /// </summary>
    public DeferredProviderResult Query(ProviderQuery query, RuntimeBindings? bindings = null)
    {
        Dictionary<string, IAsyncEnumerable<object>> collections = new(StringComparer.Ordinal);
        Dictionary<string, Func<object, List<object>>> extractors = CodeBindings.BuildExtractors();
        RuntimeBindings effectiveBindings = bindings ?? CodeBindings.Build();
        Dictionary<string, Dictionary<string, Func<object, object?>>> accessors =
            effectiveBindings.Accessors;
        Dictionary<string, string> itemTypes = CodeCollectionBuilder.GetCollectionItemTypes();
        foreach ((string? name, Func<object, List<object>>? extractor) in extractors)
        {
            if (query.Collection is not null && query.Collection != name)
            {
                continue;
            }

            FilterExpression? filter = query.CollectionFilters?.GetValueOrDefault(name) ?? query.Filter;
            collections[name] = Enumerate(name, extractor, filter,
                new AsyncProviderFilter(effectiveBindings));
        }
        if (_projects is not null && (query.Collection is null or "Projects"))
        {
            FilterExpression? filter = query.CollectionFilters?.GetValueOrDefault("Projects") ?? query.Filter;
            Func<object, bool>? predicate = filter is null ? null : Cop.Lang.FilterCompiler.Compile(filter, accessors["Project"]);
            collections["Projects"] = Projects(predicate);
        }
        return new(collections);
    }

    private async IAsyncEnumerable<object> Enumerate(
        string collection, Func<object, List<object>> extract, FilterExpression? filter,
        AsyncProviderFilter evaluator,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (string path in Paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SourceFile? file = await ReadFileAsync(path, cancellationToken).ConfigureAwait(false);
            if (file is null)
            {
                continue;
            }

            IEnumerable<object> items;
            if (collection == "Types")
            {
                items = file.Types;
            }
            else if (collection == "Statements")
            {
                items = file.Statements;
            }
            else if (collection == "Calls")
            {
                items = file.Statements.Where(static statement => statement.Kind == "call");
            }
            else
            {
                if (collection is "Methods" or "Members" or "Api")
                {
                    await PrepareMembersAsync(file.Types, cancellationToken).ConfigureAwait(false);
                }
                (string path, string collection) key = (path, collection);
                if (!_collections.TryGetValue(key, out List<object>? projected))
                {
                    projected = extract(file);
                    _collections.Add(key, projected);
                }
                items = projected;
            }
            foreach (object item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await evaluator.MatchesAsync(item, filter, cancellationToken)
                    .ConfigureAwait(false))
                {
                    yield return item;
                }
            }
        }
    }

    private async IAsyncEnumerable<object> Projects(
        Func<object, bool>? predicate,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        List<ProjectInfo> projects = await _projects!.GetValueAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (ProjectInfo project in projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (predicate is null || predicate(project))
            {
                yield return project;
            }
        }

    }

    private static async ValueTask PrepareMembersAsync(
        IEnumerable<TypeDeclaration> types, CancellationToken cancellationToken)
    {
        foreach (TypeDeclaration type in types)
        {
            await type.EnsureMembersAsync(cancellationToken).ConfigureAwait(false);
            await PrepareMembersAsync(type.NestedTypes, cancellationToken).ConfigureAwait(false);
        }
    }
}
