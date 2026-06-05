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

                for (int i = 0; i < normalizedFile.Statements.Count; i++)
                {
                    normalizedFile.Statements[i].File = normalizedFile;
                    // Populate CopIgnore from comment on previous line
                    var stmtLine = normalizedFile.Statements[i].Line;
                    if (stmtLine >= 2 && normalizedFile.CommentLines.Contains(stmtLine - 1))
                    {
                        var prevLineText = normalizedFile.Lines[stmtLine - 2]; // 0-indexed
                        var idx = prevLineText.IndexOf("cop-ignore:", StringComparison.Ordinal);
                        if (idx >= 0)
                            normalizedFile.Statements[i].CopIgnore = prevLineText[(idx + "cop-ignore:".Length)..].Trim();
                    }
                }

                for (int i = 0; i < normalizedFile.Types.Count; i++)
                    normalizedFile.Types[i] = normalizedFile.Types[i] with { File = normalizedFile };

                sourceFiles.Add(normalizedFile);
            });

        if (!parseErrors.IsEmpty)
        {
            foreach (var err in parseErrors)
                Console.Error.WriteLine(err);
        }

        var sorted = sourceFiles.OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
        return ExtractCollections(sorted, query.Collection, query.CollectionFilters);
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
