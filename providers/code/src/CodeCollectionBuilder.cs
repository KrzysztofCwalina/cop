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
    /// <summary>
    /// Scans source files under rootPath, parses them, and returns flat collections.
    /// </summary>
    public static Dictionary<string, List<object>> CollectAndParse(SourceParserRegistry parsers, ProviderQuery query)
    {
        if (query.RootPath is null)
            return new();

        var rootPath = query.RootPath;
        var excluded = query.ExcludedDirectories;

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
        var fingerprint = ComputeFingerprint(rootPath, filePaths);
        var cachePath = GetCachePath(rootPath);
        var cachedFiles = SourceCacheSerializer.TryLoad(cachePath, fingerprint);

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

            // Save to cache for next run
            try { SourceCacheSerializer.Save(cachePath, fingerprint, sorted); }
            catch { /* cache save failure is non-fatal */ }
        }

        return ExtractCollections(sorted, query.Collection, query.CollectionFilters);
    }

    /// <summary>
    /// Parses all discovered source files in parallel. Used on cache miss.
    /// </summary>
    private static List<SourceFile> ParseAllFiles(List<string> filePaths, string rootPath, SourceParserRegistry parsers)
    {
        var parseErrors = new System.Collections.Concurrent.ConcurrentBag<string>();
        var sourceFiles = new System.Collections.Concurrent.ConcurrentBag<SourceFile>();
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
                    sourceFile = parser.Parse(filePath, text);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    parseErrors.Add($"Failed to parse '{filePath}': {ex.Message}");
                    return;
                }

                if (sourceFile == null) return;

                var relativePath = Path.GetRelativePath(rootPath, filePath).Replace('\\', '/');
                var normalizedFile = sourceFile with { Path = relativePath };
                LinkReferences(normalizedFile);
                sourceFiles.Add(normalizedFile);
            });

        if (!parseErrors.IsEmpty)
        {
            foreach (var err in parseErrors)
                Console.Error.WriteLine(err);
        }

        return sourceFiles.OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Restores source files from cache, re-linking references.
    /// RawText is included in the cache, so no file re-reading needed.
    /// </summary>
    private static List<SourceFile> RestoreFromCache(List<SourceFile> cachedFiles, string rootPath, List<string> filePaths)
    {
        foreach (var file in cachedFiles)
            LinkReferences(file);
        return cachedFiles;
    }

    /// <summary>
    /// Links File/CopIgnore references on types and statements after construction/cache load.
    /// </summary>
    private static void LinkReferences(SourceFile file)
    {
        for (int i = 0; i < file.Statements.Count; i++)
        {
            file.Statements[i].File = file;
            var stmtLine = file.Statements[i].Line;
            if (stmtLine >= 2 && file.CommentLines.Contains(stmtLine - 1))
            {
                var prevLineText = file.Lines[stmtLine - 2]; // 0-indexed
                var idx = prevLineText.IndexOf("cop-ignore:", StringComparison.Ordinal);
                if (idx >= 0)
                    file.Statements[i].CopIgnore = prevLineText[(idx + "cop-ignore:".Length)..].Trim();
            }
        }

        for (int i = 0; i < file.Types.Count; i++)
            file.Types[i] = file.Types[i] with { File = file };

        for (int i = 0; i < file.Regions.Count; i++)
        {
            if (file.Regions[i].File is null)
                file.Regions[i] = file.Regions[i] with { File = file };
        }
    }

    /// <summary>
    /// Computes a fingerprint from file stats (paths, sizes, modification times).
    /// This is fast — only stat calls, no file reading.
    /// </summary>
    private static byte[] ComputeFingerprint(string rootPath, List<string> filePaths)
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
    private static string GetCachePath(string rootPath)
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
    {
        var extractors = CodeBindings.BuildExtractors();
        var accessors = collectionFilters is not null ? CodeBindings.BuildAccessors() : null;
        var collectionItemTypes = GetCollectionItemTypes();
        var collections = new Dictionary<string, List<object>>();

        foreach (var (name, extractor) in extractors)
        {
            if (collection != null && collection != name)
                continue;

            // Compile a filter predicate for this collection if available
            Func<object, bool>? predicate = null;
            if (collectionFilters is not null &&
                collectionFilters.TryGetValue(name, out var filter) &&
                collectionItemTypes.TryGetValue(name, out var itemType) &&
                accessors!.TryGetValue(itemType, out var typeAccessors))
            {
                predicate = FilterCompiler.Compile(filter, typeAccessors);
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
    private static Dictionary<string, string> GetCollectionItemTypes() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Types"] = "Type",
        ["Statements"] = "Statement",
        ["Calls"] = "Statement",
        ["Lines"] = "Line",
        ["Files"] = "File",
        ["Members"] = "Member",
        ["Api"] = "Api",
        ["Regions"] = "Region",
        ["Projects"] = "Project",
    };

    private static void CollectSourceFiles(string rootDir, SourceParserRegistry parsers, IReadOnlySet<string>? excluded, List<string> result, bool isRoot = true)
    {
        // Use EnumerateFiles with AllDirectories for a single enumeration handle.
        // This is more resilient to transient filesystem filter driver interference on Windows
        // (antivirus, indexer) compared to per-directory GetFiles + GetDirectories recursion.
        try
        {
            foreach (var file in Directory.EnumerateFiles(rootDir, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = !isRoot,  // propagate root errors, skip subdirectory errors
                AttributesToSkip = FileAttributes.System
            }))
            {
                // Check excluded directories in the path
                if (excluded is not null)
                {
                    var relativePath = file.AsSpan(rootDir.Length);
                    if (IsInExcludedDirectory(relativePath, excluded))
                        continue;
                }

                var ext = Path.GetExtension(file);
                if (parsers.GetParser(ext) != null)
                    result.Add(file);
            }
        }
        catch (UnauthorizedAccessException) when (!isRoot) { }
        catch (IOException) when (!isRoot) { }
    }

    private static bool IsInExcludedDirectory(ReadOnlySpan<char> relativePath, IReadOnlySet<string> excluded)
    {
        // Check each path segment against excluded set
        var pathStr = relativePath.ToString();
        var start = 0;
        for (int i = 0; i <= pathStr.Length; i++)
        {
            if (i == pathStr.Length || pathStr[i] == Path.DirectorySeparatorChar || pathStr[i] == Path.AltDirectorySeparatorChar)
            {
                if (i > start)
                {
                    var segment = pathStr.Substring(start, i - start);
                    if (excluded.Contains(segment))
                        return true;
                }
                start = i + 1;
            }
        }
        return false;
    }
}
