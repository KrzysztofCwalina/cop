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
        Dictionary<string, List<SourceFile>>> Snapshots = new();
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<DataProvider,
        Dictionary<string, DeferredSourceSnapshot>> DeferredSnapshots = new();

    public static DeferredProviderResult CollectDeferred(SourceParserRegistry parsers, ProviderQuery query,
        DataProvider owner, Func<List<ProjectInfo>>? discoverProjects = null)
    {
        if (query.RootPath is null) return new(new Dictionary<string, IEnumerable<object>>());
        var key = Path.GetFullPath(query.RootPath) + "\n" +
            string.Join("\n", (query.ExcludedDirectories ?? new HashSet<string>()).Order(StringComparer.Ordinal));
        var snapshots = DeferredSnapshots.GetOrCreateValue(owner);
        if (!snapshots.TryGetValue(key, out var snapshot))
        {
            snapshot = new(parsers, query, discoverProjects: discoverProjects);
            snapshots.Add(key, snapshot);
        }
        return snapshot.Query(query, owner.GetRuntimeBindings());
    }

    /// <summary>
    /// Scans source files under rootPath, parses them, and returns flat collections.
    /// </summary>
    public static Dictionary<string, List<object>> CollectAndParse(SourceParserRegistry parsers, ProviderQuery query)
        => CollectAndParse(parsers, query, null);

    public static Dictionary<string, List<object>> CollectAndParse(SourceParserRegistry parsers, ProviderQuery query, DataProvider? owner)
    {
        if (query.RootPath is null)
            return new();

        var rootPath = query.RootPath;
        var excluded = query.ExcludedDirectories;
        var snapshotKey = Path.GetFullPath(rootPath) + "\n" +
            string.Join("\n", (excluded ?? new HashSet<string>()).Order(StringComparer.Ordinal));
        var snapshots = owner is null ? null : Snapshots.GetOrCreateValue(owner);
        if (snapshots?.TryGetValue(snapshotKey, out var snapshot) == true)
            return ExtractCollections(snapshot, query.Collection, query.CollectionFilters, query.Filter);

        var filePaths = new List<string>();
        CollectSourceFiles(rootPath, parsers, excluded, filePaths);

        // Retry with exponential backoff if 0 files found but directory is non-empty.
        // Handles transient filesystem filter driver interference (antivirus, indexer) on Windows.
        if (filePaths.Count == 0 && Directory.Exists(rootPath))
        {
            try
            {
                var hasAnyEntries = Directory.EnumerateFileSystemEntries(rootPath).Any();
                if (hasAnyEntries)
                {
                    int[] retryDelaysMs = [200, 1000, 3000];
                    foreach (var delay in retryDelaysMs)
                    {
                        Thread.Sleep(delay);
                        CollectSourceFiles(rootPath, parsers, excluded, filePaths);
                        if (filePaths.Count > 0) break;
                    }
                    if (filePaths.Count == 0)
                    {
                        Console.Error.WriteLine($"Error: Provider scan found 0 source files in '{rootPath}' after 3 retries. " +
                            $"This likely indicates filesystem interference (antivirus, file locks). Results are unreliable.");
                    }
                }
            }
            catch { /* ignore retry errors — the original empty result stands */ }
        }

        // Compute cache fingerprint from file stats (fast: just stat calls, no file reading)
        var fingerprint = ComputeFingerprint(rootPath, filePaths, parsers);
        var canCache = filePaths.All(path => parsers.GetParser(Path.GetExtension(path))!.CanCache(path));
        var cachePath = GetCachePath(rootPath);
        var cachedFiles = canCache
            ? SourceCacheSerializer.TryLoad(cachePath, fingerprint, owner?.GetType().Assembly ?? parsers.CacheAssembly) : null;

        List<SourceFile> sorted;
        if (cachedFiles != null)
        {
            // Cache hit: re-read raw text for each file and re-link references
            sorted = RestoreFromCache(cachedFiles, rootPath, filePaths);
        }
        else
        {
            // Cache miss: parse all files
            sorted = ParseAllFiles(filePaths, rootPath, parsers);

            // Don't cache a parse that produced errors: the file-stats fingerprint won't change until
            // the file is fixed, so re-parsing each run keeps the error surfaced (a cached run would
            // restore the partial model with no error).
            if (canCache && !sorted.Any(f => f.ParseErrors.Count > 0))
            {
                // Save to cache for next run
                try { SourceCacheSerializer.Save(cachePath, fingerprint, sorted); }
                catch { /* cache save failure is non-fatal */ }
            }
        }

        foreach (var file in sorted)
        {
            if (!parsers.GetParser(Path.GetExtension(file.Path))!.SupportsCrossFileMembers) continue;
            void DeferMembers(TypeDeclaration type)
            {
                type.DeferMembers(() => ResolveReceiverMembers(type, file, sorted));
                foreach (var nested in type.NestedTypes) DeferMembers(nested);
            }
            foreach (var type in file.Types) DeferMembers(type);
        }
        snapshots?.Add(snapshotKey, sorted);
        return ExtractCollections(sorted, query.Collection, query.CollectionFilters, query.Filter);
    }

    /// <summary>
    /// Parses all discovered source files in parallel. Used on cache miss.
    /// </summary>
    private static List<SourceFile> ParseAllFiles(List<string> filePaths, string rootPath, SourceParserRegistry parsers)
    {
        var parseErrors = new System.Collections.Concurrent.ConcurrentBag<string>();
        var sourceFiles = new System.Collections.Concurrent.ConcurrentBag<SourceFile>();
        var diagThresholdMs = GetParseDiagThresholdMs();
        Parallel.ForEach(filePaths,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            filePath =>
            {
                var ext = Path.GetExtension(filePath);
                var parser = parsers.GetParser(ext);
                if (parser == null) return;

                SourceFile? sourceFile;
                try
                {
                    using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(stream);
                    var text = reader.ReadToEnd();
                    if (diagThresholdMs >= 0)
                    {
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        sourceFile = parser.Parse(filePath, text);
                        sw.Stop();
                        if (sw.ElapsedMilliseconds >= diagThresholdMs)
                            Console.Error.WriteLine($"[parse-diag] {sw.ElapsedMilliseconds,6} ms  {text.Length,8} chars  {filePath}");
                    }
                    else
                    {
                        sourceFile = parser.Parse(filePath, text);
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    parseErrors.Add($"{filePath}: error: failed to parse — {ex.Message}");
                    return;
                }

                if (sourceFile == null) return;

                var relativePath = Path.GetRelativePath(rootPath, filePath).Replace('\\', '/');
                var normalizedFile = sourceFile with
                {
                    Path = relativePath, FullPath = Path.GetFullPath(filePath), SourceRoot = Path.GetFullPath(rootPath)
                };
                LinkReferences(normalizedFile);
                sourceFiles.Add(normalizedFile);

                // A parser that detected syntax errors (e.g. Roslyn) reports them here so they are
                // not hidden behind the partial model it still produced.
                foreach (var pe in normalizedFile.ParseErrors)
                    parseErrors.Add(pe);
            });

        // Forward parse errors to the engine via the shared sink so a file that failed to parse is
        // surfaced as a prominent warning instead of being silently skipped or modelled as partial
        // data. (The engine drains the sink into result.Warnings — visible but non-fatal, because a
        // repo may legitimately contain .cs snippet/region fixtures that aren't compilable units.)
        if (!parseErrors.IsEmpty)
        {
            foreach (var err in parseErrors)
                Cop.Core.ProviderErrors.Report(err);
        }

        return sourceFiles.OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Returns the per-file parse timing threshold in ms when -dd (CopDiagnostics.Timing) is set
    /// (logs files slower than 100ms). Returns -1 when disabled.
    /// </summary>
    private static long GetParseDiagThresholdMs()
        => Cop.Core.CopDiagnostics.Timing ? 100 : -1;

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
        foreach (var file in cachedFiles)
            LinkReferences(file);
        return cachedFiles;
    }

    /// <summary>
    /// Links File/CopIgnore references on types and statements after construction/cache load.
    /// </summary>
    internal static void LinkReferences(SourceFile file)
    {
        var seen = new HashSet<StatementInfo>(ReferenceEqualityComparer.Instance);
        void LinkStatement(StatementInfo statement, MethodDeclaration? method, StatementInfo? parent)
        {
            if (method is not null) statement.Method = method;
            if (parent is not null) statement.Parent = parent;
            if (!seen.Add(statement)) return;
            statement.File = file;
            var stmtLine = statement.Line;
            if (stmtLine >= 2 && stmtLine <= file.Lines.Count + 1 && file.CommentLines.Contains(stmtLine - 1))
            {
                var prevLineText = file.Lines[stmtLine - 2]; // 0-indexed
                var idx = prevLineText.IndexOf("cop-ignore:", StringComparison.Ordinal);
                if (idx >= 0)
                    statement.CopIgnore = prevLineText[(idx + "cop-ignore:".Length)..].Trim();
            }
            foreach (var child in statement.Children)
                LinkStatement(child, method ?? statement.Method, statement);
        }

        void LinkMethod(MethodDeclaration method)
        {
            method.File = file;
            foreach (var statement in method.Statements)
                LinkStatement(statement, method, null);
        }
        void LinkType(TypeDeclaration type)
        {
            type.File = file;
            foreach (var method in type.DeclaredMethods) LinkMethod(method);
            foreach (var constructor in type.Constructors) LinkMethod(constructor);
            foreach (var nested in type.NestedTypes) LinkType(nested);
        }
        foreach (var type in file.Types) LinkType(type);
        foreach (var method in file.Methods)
            LinkMethod(method);
        foreach (var statement in file.Statements)
            LinkStatement(statement, null, null);

        for (int i = 0; i < file.Regions.Count; i++)
        {
            if (file.Regions[i].File is null)
                file.Regions[i] = file.Regions[i] with { File = file };
        }
    }

    internal static List<MethodDeclaration> ResolveReceiverMembers(
        TypeDeclaration type, SourceFile owner, IEnumerable<SourceFile> sourceFiles)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var scope = type.Namespace ?? owner.Namespace;
        var files = sourceFiles.Where(file => file.Language == owner.Language
            && comparer.Equals(Path.GetDirectoryName(file.FullPath), Path.GetDirectoryName(owner.FullPath))).ToList();
        IEnumerable<TypeDeclaration> AllTypes(IEnumerable<TypeDeclaration> types)
        {
            foreach (var candidate in types)
            {
                yield return candidate;
                foreach (var nested in AllTypes(candidate.NestedTypes)) yield return nested;
            }
        }
        var owners = files.SelectMany(file => AllTypes(file.Types)
            .Where(candidate => candidate.Name == type.Name && (candidate.Namespace ?? file.Namespace) == scope))
            .Distinct<TypeDeclaration>(ReferenceEqualityComparer.Instance).ToList();
        if (owners.Count != 1 || !ReferenceEquals(owners[0], type))
            return type.DeclaredMethods;
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
        using var sha = SHA256.Create();
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        // Sort for deterministic fingerprint regardless of discovery order
        var sorted = filePaths.OrderBy(p => p, StringComparer.Ordinal);
        foreach (var path in sorted)
        {
            var relative = Path.GetRelativePath(rootPath, path);
            writer.Write(relative);
            writer.Write(parsers.GetParser(Path.GetExtension(path))!.GetCacheIdentity(path));
            try
            {
                var info = new FileInfo(path);
                writer.Write(info.Length);
                writer.Write(info.LastWriteTimeUtc.Ticks);
            }
            catch
            {
                writer.Write(0L);
                writer.Write(0L);
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
        var hash = Convert.ToHexStringLower(
            SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(rootPath)));
        var cacheDir = Path.Combine(
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

    public static Dictionary<string, List<object>> ExtractCollections(
        List<SourceFile> sourceFiles, string? collection,
        IReadOnlyDictionary<string, FilterExpression>? collectionFilters,
        FilterExpression? filter)
    {
        var extractors = CodeBindings.BuildExtractors();
        var accessors = collectionFilters is not null || filter is not null ? CodeBindings.BuildAccessors() : null;
        var collectionItemTypes = GetCollectionItemTypes();
        var collections = new Dictionary<string, List<object>>();

        foreach (var (name, extractor) in extractors)
        {
            if (collection != null && collection != name)
                continue;

            // Compile a filter predicate for this collection if available
            Func<object, bool>? predicate = null;
            var effectiveFilter = collectionFilters?.GetValueOrDefault(name) ?? filter;
            if (effectiveFilter is not null &&
                collectionItemTypes.TryGetValue(name, out var itemType) &&
                accessors!.TryGetValue(itemType, out var typeAccessors))
            {
                predicate = FilterCompiler.Compile(effectiveFilter, typeAccessors);
            }

            var items = new List<object>();
            foreach (var file in sourceFiles)
            {
                if (predicate is null)
                {
                    items.AddRange(extractor(file));
                }
                else
                {
                    foreach (var item in extractor(file))
                    {
                        if (predicate(item))
                            items.Add(item);
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

    public static void CollectSourceFiles(string rootDir, SourceParserRegistry parsers, IReadOnlySet<string>? excluded, List<string> result, bool isRoot = true)
    {
        // Prune excluded directories (node_modules, bin, obj, .git, dist, ...) DURING traversal
        // instead of enumerating the whole tree and filtering paths afterward. This is critical:
        // a single JS repo node_modules can hold millions of files, and walking it would dominate
        // runtime (observed: 8M files / ~49s for one azure-sdk-for-js package). We enumerate each
        // directory's files, then recurse only into non-excluded, non-reparse-point subdirectories.
        try
        {
            foreach (var file in Directory.EnumerateFiles(rootDir, "*", new EnumerationOptions
            {
                RecurseSubdirectories = false,
                IgnoreInaccessible = !isRoot,  // propagate root errors, skip subdirectory errors
                AttributesToSkip = FileAttributes.System
            }))
            {
                var ext = Path.GetExtension(file);
                if (parsers.GetParser(ext) != null)
                    result.Add(file);
            }
        }
        catch (UnauthorizedAccessException) when (!isRoot) { }
        catch (IOException) when (!isRoot) { }

        IEnumerable<string> subDirs;
        try
        {
            subDirs = Directory.EnumerateDirectories(rootDir, "*", new EnumerationOptions
            {
                RecurseSubdirectories = false,
                IgnoreInaccessible = !isRoot,
                // Skip reparse points (symlinks/junctions) to avoid infinite loops in pnpm-style node_modules.
                AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint
            });
        }
        catch (UnauthorizedAccessException) when (!isRoot) { return; }
        catch (IOException) when (!isRoot) { return; }

        foreach (var subDir in subDirs)
        {
            var dirName = Path.GetFileName(subDir);
            if (excluded is not null && excluded.Contains(dirName))
                continue;
            CollectSourceFiles(subDir, parsers, excluded, result, isRoot: false);
        }
    }
}
