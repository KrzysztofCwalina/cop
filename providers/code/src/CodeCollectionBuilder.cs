using Cop.Core;
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
                    normalizedFile.Statements[i].File = normalizedFile;

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
        return ExtractCollections(sorted, query.RequestedCollections);
    }

    /// <summary>
    /// Extracts flat collections from a list of parsed source files.
    /// </summary>
    public static Dictionary<string, List<object>> ExtractCollections(List<SourceFile> sourceFiles, IReadOnlyList<string>? requestedCollections)
    {
        var extractors = CodeBindings.BuildExtractors();
        var collections = new Dictionary<string, List<object>>();

        foreach (var (name, extractor) in extractors)
        {
            if (requestedCollections != null && !requestedCollections.Contains(name))
                continue;

            var items = new List<object>();
            foreach (var file in sourceFiles)
                items.AddRange(extractor(file));
            collections[name] = items;
        }

        return collections;
    }

    private static void CollectSourceFiles(string dir, SourceParserRegistry parsers, IReadOnlySet<string>? excluded, List<string> result)
    {
        try
        {
            foreach (var file in Directory.GetFiles(dir))
            {
                var ext = Path.GetExtension(file);
                if (parsers.GetParser(ext) != null)
                    result.Add(file);
            }

            foreach (var subDir in Directory.GetDirectories(dir))
            {
                var dirName = Path.GetFileName(subDir);
                if (excluded is not null && excluded.Contains(dirName)) continue;
                CollectSourceFiles(subDir, parsers, excluded, result);
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
    }
}
