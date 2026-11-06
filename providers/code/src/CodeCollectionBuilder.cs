// Discovers source files and builds the shared code-model collections.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using Cop.Core;
using Cop.Lang;
using Cop.Providers.SourceModel;
using Cop.Providers.SourceParsers;

namespace Cop.Providers;

/// <summary>
/// Shared collection builder for code analysis providers.
/// Scans source files, parses them using a SourceParserRegistry, and returns flat collections.
/// </summary>
public static class CodeCollectionBuilder
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<DataProvider,
        Dictionary<string, List<SourceFile>>> s_snapshots = new();
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<DataProvider,
        Dictionary<string, DeferredSourceSnapshot>> s_deferredSnapshots = new();

    /// <summary>
    /// Captures source paths and defers parsing until the query demands a collection.
    /// </summary>
    public static DeferredProviderResult CollectDeferred(SourceParserRegistry parsers, ProviderQuery query,
        DataProvider owner,
        Func<CancellationToken, ValueTask<List<ProjectInfo>>>? discoverProjects = null)
    {
        if (query.RootPath is null)
        {
            return new(new Dictionary<string, IAsyncEnumerable<object>>());
        }

        string key = Path.GetFullPath(query.RootPath) + "\n" +
            string.Join("\n", (query.ExcludedDirectories ?? new HashSet<string>()).Order(StringComparer.Ordinal));
        Dictionary<string, DeferredSourceSnapshot> snapshots = s_deferredSnapshots.GetOrCreateValue(owner);
        if (!snapshots.TryGetValue(key, out DeferredSourceSnapshot? snapshot))
        {
            snapshot = new(parsers, query, discoverProjects: discoverProjects);
            snapshots.Add(key, snapshot);
        }
        return snapshot.Query(query, owner.GetRuntimeBindings());
    }

    /// <summary>
    /// Scans source files under rootPath, parses them, and returns flat collections.
    /// </summary>
    public static ValueTask<Dictionary<string, List<object>>> CollectAndParseAsync(
        SourceParserRegistry parsers, ProviderQuery query,
        CancellationToken cancellationToken = default)
        => CollectAndParseAsync(parsers, query, null, cancellationToken);

    /// <summary>
    /// Collects matching source files and parses them with the registered language
    /// parsers.
    /// </summary>
    public static async ValueTask<Dictionary<string, List<object>>> CollectAndParseAsync(
        SourceParserRegistry parsers, ProviderQuery query, DataProvider? owner,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (query.RootPath is null)
        {
            return new();
        }

        string rootPath = query.RootPath;
        IReadOnlySet<string>? excluded = query.ExcludedDirectories;
        string snapshotKey = Path.GetFullPath(rootPath) + "\n" +
            string.Join("\n", (excluded ?? new HashSet<string>()).Order(StringComparer.Ordinal));
        Dictionary<string, List<SourceFile>>? snapshots = owner is null ? null : s_snapshots.GetOrCreateValue(owner);
        if (snapshots?.TryGetValue(snapshotKey, out List<SourceFile>? snapshot) == true)
        {
            return await ExtractCollectionsAsync(snapshot, query, owner, cancellationToken)
                .ConfigureAwait(false);
        }

        List<string> filePaths = new List<string>();
        CollectSourceFiles(rootPath, parsers, excluded, filePaths);

        // Retry with exponential backoff if 0 files found but directory is non-empty.
        // Handles transient filesystem filter driver interference (antivirus, indexer) on Windows.
        if (filePaths.Count == 0 && Directory.Exists(rootPath))
        {
            try
            {
                bool hasAnyEntries = Directory.EnumerateFileSystemEntries(rootPath).Any();
                if (hasAnyEntries)
                {
                    int[] retryDelaysMs = [200, 1000, 3000];
                    foreach (int delay in retryDelaysMs)
                    {
                        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                        CollectSourceFiles(rootPath, parsers, excluded, filePaths);
                        if (filePaths.Count > 0)
                        {
                            break;
                        }
                    }
                    if (filePaths.Count == 0)
                    {
                        Console.Error.WriteLine($"Error: Provider scan found 0 source files in '{rootPath}' after 3 retries. " +
                            $"This likely indicates filesystem interference (antivirus, file locks). Results are unreliable.");
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ProviderErrors.Report($"Could not retry source discovery in '{rootPath}': {ex.Message}");
            }
        }

        // Compute cache fingerprint from file stats (fast: just stat calls, no file reading)
        byte[] fingerprint = ComputeFingerprint(rootPath, filePaths, parsers);
        bool canCache = filePaths.All(path => parsers.GetParser(Path.GetExtension(path))!.CanCache(path));
        string cachePath = GetCachePath(rootPath);
        List<SourceFile>? cachedFiles = canCache
            ? SourceCacheSerializer.LoadOrDefault(cachePath, fingerprint, owner?.GetType().Assembly ?? parsers.CacheAssembly) : null;

        List<SourceFile> sorted;
        if (cachedFiles != null)
        {
            // Cache hit: re-read raw text for each file and re-link references
            sorted = RestoreFromCache(cachedFiles, rootPath, filePaths);
        }
        else
        {
            // Cache miss: parse all files
            sorted = await ParseAllFilesAsync(filePaths, rootPath, parsers, cancellationToken)
                .ConfigureAwait(false);

            // Don't cache a parse that produced errors: the file-stats fingerprint won't change until
            // the file is fixed, so re-parsing each run keeps the error surfaced (a cached run would
            // restore the partial model with no error).
            if (canCache && !sorted.Any(f => f.ParseErrors.Count > 0))
            {
                // Save to cache for next run
                try { SourceCacheSerializer.Save(cachePath, fingerprint, sorted); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Console.Error.WriteLine($"Warning: Could not cache source models: {ex.Message}");
                }
            }
        }

        foreach (SourceFile file in sorted)
        {
            if (!parsers.GetParser(Path.GetExtension(file.Path))!.SupportsCrossFileMembers)
            {
                continue;
            }

            void DeferMembers(TypeDeclaration type)
            {
                type.DeferMembers(() => ResolveReceiverMembers(type, file, sorted));
                foreach (TypeDeclaration nested in type.NestedTypes)
                {
                    DeferMembers(nested);
                }
            }
            foreach (TypeDeclaration type in file.Types)
            {
                DeferMembers(type);
            }
        }
        snapshots?.Add(snapshotKey, sorted);
        return await ExtractCollectionsAsync(sorted, query, owner, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async ValueTask<Dictionary<string, List<object>>> ExtractCollectionsAsync(
        List<SourceFile> sourceFiles, ProviderQuery query, DataProvider? owner,
        CancellationToken cancellationToken)
    {
        Dictionary<string, List<object>> collections = ExtractCollections(
            sourceFiles, query.Collection);
        AsyncProviderFilter evaluator = new(owner?.GetRuntimeBindings() ?? CodeBindings.Build());
        foreach (string collection in collections.Keys.ToArray())
        {
            FilterExpression? filter =
                query.CollectionFilters?.GetValueOrDefault(collection) ?? query.Filter;
            if (filter is null)
            {
                continue;
            }
            List<object> accepted = [];
            foreach (object item in collections[collection])
            {
                if (await evaluator.MatchesAsync(item, filter, cancellationToken)
                    .ConfigureAwait(false))
                {
                    accepted.Add(item);
                }
            }
            collections[collection] = accepted;
        }
        return collections;
    }

    /// <summary>
    /// Parses all discovered source files in parallel. Used on cache miss.
    /// </summary>
    private static async ValueTask<List<SourceFile>> ParseAllFilesAsync(
        List<string> filePaths, string rootPath, SourceParserRegistry parsers,
        CancellationToken cancellationToken)
    {
        ConcurrentBag<string> parseErrors = new System.Collections.Concurrent.ConcurrentBag<string>();
        ConcurrentBag<SourceFile> sourceFiles = new System.Collections.Concurrent.ConcurrentBag<SourceFile>();
        long diagThresholdMs = GetParseDiagThresholdMs();
        await Parallel.ForEachAsync(filePaths,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Environment.ProcessorCount,
                CancellationToken = cancellationToken
            },
            async (filePath, token) =>
            {
                string ext = Path.GetExtension(filePath);
                ISourceParser? parser = parsers.GetParser(ext);
                if (parser == null)
                {
                    return;
                }

                SourceFile? sourceFile;
                try
                {
                    using FileStream stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using StreamReader reader = new StreamReader(stream);
                    string text = await reader.ReadToEndAsync(token).ConfigureAwait(false);
                    if (diagThresholdMs >= 0)
                    {
                        Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
                        sourceFile = await parser.ParseAsync(filePath, text, token)
                            .ConfigureAwait(false);
                        sw.Stop();
                        if (sw.ElapsedMilliseconds >= diagThresholdMs)
                        {
                            Console.Error.WriteLine($"[parse-diag] {sw.ElapsedMilliseconds,6} ms  {text.Length,8} chars  {filePath}");
                        }
                    }
                    else
                    {
                        sourceFile = await parser.ParseAsync(filePath, text, token)
                            .ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    parseErrors.Add($"{filePath}: error: failed to parse — {ex.Message}");
                    return;
                }

                if (sourceFile == null)
                {
                    return;
                }

                string relativePath = Path.GetRelativePath(rootPath, filePath).Replace('\\', '/');
                SourceFile normalizedFile = sourceFile with
                {
                    Path = relativePath, FullPath = Path.GetFullPath(filePath), SourceRoot = Path.GetFullPath(rootPath)
                };
                LinkReferences(normalizedFile);
                sourceFiles.Add(normalizedFile);

                // A parser that detected syntax errors (e.g. Roslyn) reports them here so they are
                // not hidden behind the partial model it still produced.
                foreach (string pe in normalizedFile.ParseErrors)
                {
                    parseErrors.Add(pe);
                }
            }).ConfigureAwait(false);

        // Forward parse errors to the engine via the shared sink so a file that failed to parse is
        // surfaced as a prominent warning instead of being silently skipped or modelled as partial
        // data. (The engine drains the sink into result.Warnings — visible but non-fatal, because a
        // repo may legitimately contain .cs snippet/region fixtures that aren't compilable units.)
        if (!parseErrors.IsEmpty)
        {
            foreach (string err in parseErrors)
            {
                Cop.Core.ProviderErrors.Report(err);
            }
        }

        return sourceFiles.OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Returns the per-file parse timing threshold when diagnostic timing is enabled
    /// (logs files slower than 100ms). Returns -1 when disabled.
    /// </summary>
    private static long GetParseDiagThresholdMs()
        => Cop.Core.CopDiagnostics.IsTimingEnabled ? 100 : -1;

    /// <summary>
    /// Restores source files from cache, re-linking references.
    /// RawText is included in the cache, so no file re-reading needed.
    /// </summary>
    private static List<SourceFile> RestoreFromCache(List<SourceFile> cachedFiles, string rootPath, List<string> filePaths)
    {
        cachedFiles = cachedFiles.Select(file => file with
        {
            FullPath = Path.GetFullPath(Path.Combine(rootPath, file.Path)), SourceRoot = Path.GetFullPath(rootPath)
        }).ToList();
        foreach (SourceFile file in cachedFiles)
        {
            LinkReferences(file);
        }

        return cachedFiles;
    }

    /// <summary>
    /// Links File/CopIgnore references on types and statements after construction/cache load.
    /// </summary>
    internal static void LinkReferences(SourceFile file)
    {
        HashSet<StatementInfo> seen = new HashSet<StatementInfo>(ReferenceEqualityComparer.Instance);
        void LinkStatement(StatementInfo statement, MethodDeclaration? method, StatementInfo? parent)
        {
            if (method is not null)
            {
                statement.Method = method;
            }

            if (parent is not null)
            {
                statement.Parent = parent;
            }

            if (!seen.Add(statement))
            {
                return;
            }

            statement.File = file;
            int stmtLine = statement.Line;
            if (stmtLine >= 2 && stmtLine <= file.Lines.Count + 1 && file.CommentLines.Contains(stmtLine - 1))
            {
                string prevLineText = file.Lines[stmtLine - 2]; // 0-indexed
                int idx = prevLineText.IndexOf("cop-ignore:", StringComparison.Ordinal);
                if (idx >= 0)
                {
                    statement.CopIgnore = prevLineText[(idx + "cop-ignore:".Length)..].Trim();
                }
            }
            foreach (StatementInfo child in statement.Children)
            {
                LinkStatement(child, method ?? statement.Method, statement);
            }
        }

        void LinkMethod(MethodDeclaration method)
        {
            method.File = file;
            foreach (StatementInfo statement in method.Statements)
            {
                LinkStatement(statement, method, null);
            }
        }
        void LinkType(TypeDeclaration type)
        {
            type.File = file;
            foreach (MethodDeclaration method in type.DeclaredMethods)
            {
                LinkMethod(method);
            }

            foreach (MethodDeclaration constructor in type.Constructors)
            {
                LinkMethod(constructor);
            }

            foreach (TypeDeclaration nested in type.NestedTypes)
            {
                LinkType(nested);
            }
        }
        foreach (TypeDeclaration type in file.Types)
        {
            LinkType(type);
        }

        foreach (MethodDeclaration method in file.Methods)
        {
            LinkMethod(method);
        }

        foreach (StatementInfo statement in file.Statements)
        {
            LinkStatement(statement, null, null);
        }

        for (int i = 0; i < file.Regions.Count; i++)
        {
            if (file.Regions[i].File is null)
            {
                file.Regions[i] = file.Regions[i] with { File = file };
            }
        }
    }

    internal static List<MethodDeclaration> ResolveReceiverMembers(
        TypeDeclaration type, SourceFile owner, IEnumerable<SourceFile> sourceFiles)
    {
        StringComparer comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        string? scope = type.Namespace ?? owner.Namespace;
        List<SourceFile> files = sourceFiles.Where(file => file.Language == owner.Language
            && comparer.Equals(Path.GetDirectoryName(file.FullPath), Path.GetDirectoryName(owner.FullPath))).ToList();
        IEnumerable<TypeDeclaration> AllTypes(IEnumerable<TypeDeclaration> types)
        {
            foreach (TypeDeclaration candidate in types)
            {
                yield return candidate;
                foreach (TypeDeclaration nested in AllTypes(candidate.NestedTypes))
                {
                    yield return nested;
                }
            }
        }
        List<TypeDeclaration> owners = files.SelectMany(file => AllTypes(file.Types)
            .Where(candidate => candidate.Name == type.Name && (candidate.Namespace ?? file.Namespace) == scope))
            .Distinct<TypeDeclaration>(ReferenceEqualityComparer.Instance).ToList();
        if (owners.Count != 1 || !ReferenceEquals(owners[0], type))
        {
            return type.DeclaredMethods;
        }

        return type.DeclaredMethods.Concat(files.Where(file => file.Namespace == scope)
                .SelectMany(file => file.Methods).Where(method => method.ReceiverType == type.Name))
            .Distinct<MethodDeclaration>(ReferenceEqualityComparer.Instance).ToList();
    }

    /// <summary>
    /// Computes a fingerprint from file stats (paths, sizes, modification times).
    /// This is fast — only stat calls, no file reading.
    /// </summary>
    private static byte[] ComputeFingerprint(string rootPath, List<string> filePaths, SourceParserRegistry parsers)
    {
        using SHA256 sha = SHA256.Create();
        using MemoryStream ms = new MemoryStream();
        using BinaryWriter writer = new BinaryWriter(ms);

        // Sort for deterministic fingerprint regardless of discovery order
        IOrderedEnumerable<string> sorted = filePaths.OrderBy(p => p, StringComparer.Ordinal);
        foreach (string? path in sorted)
        {
            string relative = Path.GetRelativePath(rootPath, path);
            writer.Write(relative);
            writer.Write(parsers.GetParser(Path.GetExtension(path))!.GetCacheIdentity(path));
            try
            {
                FileInfo info = new FileInfo(path);
                writer.Write(info.Length);
                writer.Write(info.LastWriteTimeUtc.Ticks);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new IOException(
                    $"Cannot fingerprint source file '{path}': {ex.Message}", ex);
            }
        }
        writer.Flush();
        return sha.ComputeHash(ms.ToArray());
    }

    /// <summary>
    /// Gets the cache file path for a given root directory.
    /// </summary>
    internal static string GetCachePath(string rootPath)
    {
        string hash = Convert.ToHexStringLower(
            SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(rootPath)));
        string cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".cop", "cache");
        return Path.Combine(cacheDir, $"source-{hash[..16]}.bin");
    }

    /// <summary>
    /// Extracts flat collections from a list of parsed source files.
    /// When collectionFilters are provided, items are filtered inline during extraction.
    /// </summary>
    public static Dictionary<string, List<object>> ExtractCollections(
        List<SourceFile> sourceFiles, string? collection,
        IReadOnlyDictionary<string, FilterExpression>? collectionFilters = null)
        => ExtractCollections(sourceFiles, collection, collectionFilters, null);

    /// <summary>
    /// Extracts requested source collections and applies the supplied query filters.
    /// </summary>
    public static Dictionary<string, List<object>> ExtractCollections(
        List<SourceFile> sourceFiles, string? collection,
        IReadOnlyDictionary<string, FilterExpression>? collectionFilters,
        FilterExpression? filter)
    {
        foreach (SourceFile file in sourceFiles)
        {
            if (file.FullPath.Length > 0)
            {
                ExecutionStatistics.RecordInputFile(file.FullPath);
            }
        }

        Dictionary<string, Func<object, List<object>>> extractors = CodeBindings.BuildExtractors();
        Dictionary<string, Dictionary<string, Func<object, object?>>>? accessors = collectionFilters is not null || filter is not null ? CodeBindings.BuildAccessors() : null;
        Dictionary<string, string> collectionItemTypes = GetCollectionItemTypes();
        Dictionary<string, List<object>> collections = new Dictionary<string, List<object>>();

        foreach ((string? name, Func<object, List<object>>? extractor) in extractors)
        {
            if (collection != null && collection != name)
            {
                continue;
            }

            // Compile a filter predicate for this collection if available
            Func<object, bool>? predicate = null;
            FilterExpression? effectiveFilter = collectionFilters?.GetValueOrDefault(name) ?? filter;
            if (effectiveFilter is not null &&
                collectionItemTypes.TryGetValue(name, out string? itemType) &&
                accessors!.TryGetValue(itemType, out Dictionary<string, Func<object, object?>>? typeAccessors))
            {
                predicate = FilterCompiler.Compile(effectiveFilter, typeAccessors);
            }

            List<object> items = new List<object>();
            foreach (SourceFile file in sourceFiles)
            {
                if (predicate is null)
                {
                    items.AddRange(extractor(file));
                }
                else
                {
                    foreach (object item in extractor(file))
                    {
                        if (predicate(item))
                        {
                            items.Add(item);
                        }
                    }
                }
            }
            collections[name] = items;
        }

        return collections;
    }

    /// <summary>
    /// Maps collection names to their item type names (matching schema/accessors keys).
    /// </summary>
    internal static Dictionary<string, string> GetCollectionItemTypes() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Types"] = "Type",
        ["Statements"] = "Statement",
        ["Methods"] = "Method",
        ["Calls"] = "Statement",
        ["Lines"] = "Line",
        ["Files"] = "File",
        ["Members"] = "Member",
        ["Api"] = "Api",
        ["Regions"] = "Region",
        ["Projects"] = "Project",
    };

    /// <summary>
    /// Enumerates source files supported by the registered parsers beneath an analysis
    /// root.
    /// </summary>
    public static void CollectSourceFiles(string rootDir, SourceParserRegistry parsers,
        IReadOnlySet<string>? excluded, List<string> result)
    {
        // Prune excluded directories (node_modules, bin, obj, .git, dist, ...) DURING traversal
        // instead of enumerating the whole tree and filtering paths afterward. This is critical:
        // a single JS repo node_modules can hold millions of files, and walking it would dominate
        // runtime (observed: 8M files / ~49s for one azure-sdk-for-js package). We enumerate each
        // directory's files, then recurse only into non-excluded, non-reparse-point subdirectories.
        try
        {
            foreach (string file in Directory.EnumerateFiles(rootDir, "*", new EnumerationOptions
            {
                RecurseSubdirectories = false,
                IgnoreInaccessible = false,
                AttributesToSkip = FileAttributes.System
            }))
            {
                string ext = Path.GetExtension(file);
                if (parsers.GetParser(ext) != null)
                {
                    result.Add(file);
                }
            }

            IEnumerable<string> subDirs = Directory.EnumerateDirectories(
                rootDir, "*", new EnumerationOptions
            {
                RecurseSubdirectories = false,
                IgnoreInaccessible = false,
                // Skip reparse points (symlinks/junctions) to avoid infinite loops in pnpm-style node_modules.
                AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint
            });

            foreach (string subDir in subDirs)
            {
                string dirName = Path.GetFileName(subDir);
                if (excluded is not null && excluded.Contains(dirName))
                {
                    continue;
                }

                CollectSourceFiles(subDir, parsers, excluded, result);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException(
                $"Cannot enumerate source directory '{rootDir}': {ex.Message}", ex);
        }
    }
}
