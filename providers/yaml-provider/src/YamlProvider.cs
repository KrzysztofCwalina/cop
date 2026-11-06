// Exposes YAML documents and entries as source-linked provider collections.

using System.Collections.Concurrent;
using Cop.Core;
using Cop.Providers.SourceModel;

namespace Cop.Providers.Yaml;

/// <summary>
/// Provider for YAML document analysis. Scans .yaml/.yml files, flattens each mapping into
/// dotted-path key entries (with scalar values and line numbers), and returns flat CLR object
/// collections (Entries, Documents). The YAML parser is hand-rolled — no third-party library.
/// </summary>
public sealed class YamlProvider : DataProvider
{
    /// <inheritdoc />
    public override ReadOnlyMemory<byte> GetSchema() => s_schema.ToJson();

    private static readonly ProviderSchema s_schema = BuildSchema();

    private static readonly HashSet<string> s_yamlExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".yaml", ".yml" };

    private static ProviderSchema BuildSchema()
    {
        return new ProviderSchema
        {
            Types =
            [
                TypeDef("YamlEntry", null,
                    Prop("Path"), Prop("Key"), Prop("Value"),
                    Prop("Line", "int"), Prop("Document", "int"),
                    Opt("File", "File"), Prop("Source")),

                TypeDef("YamlDocument", null,
                    Prop("Index", "int"), Prop("Line", "int"),
                    Opt("File", "File"), Prop("Source")),
            ],
            Collections =
            [
                new() { Name = "Entries", ItemType = "YamlEntry" },
                new() { Name = "Documents", ItemType = "YamlDocument" },
            ]
        };
    }

    private static ProviderTypeSchema TypeDef(string name, string? baseType, params ProviderPropertySchema[] props)
        => new() { Name = name, Base = baseType, Properties = [.. props] };
    private static ProviderPropertySchema Prop(string name, string type = "string")
        => new() { Name = name, Type = type };
    private static ProviderPropertySchema Opt(string name, string type = "string")
        => new() { Name = name, Type = type, IsOptional = true };

    /// <inheritdoc />
    public override RuntimeBindings GetRuntimeBindings()
    {
        return new RuntimeBindings
        {
            ClrTypeMappings = new()
            {
                [typeof(YamlEntryInfo)] = "YamlEntry",
                [typeof(YamlDocumentInfo)] = "YamlDocument",
                [typeof(SourceFile)] = "File",
            },
            Accessors = BuildAccessors(),
            TextConverters = new()
            {
                ["File"] = o => ((SourceFile)o).Path,
            },
        };
    }

    /// <inheritdoc />
    public override async ValueTask<object?> QueryAsync(
        ProviderQuery query, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (query.RootPath is null)
        {
            return new Dictionary<string, List<object>>();
        }

        string rootPath = query.RootPath;
        IReadOnlySet<string>? excluded = query.ExcludedDirectories;

        List<string> filePaths = new List<string>();
        CollectYamlFiles(rootPath, excluded, filePaths);

        ConcurrentBag<(SourceFile File, YamlParser.YamlParseResult Doc)> parsed = new ConcurrentBag<(SourceFile File, YamlParser.YamlParseResult Doc)>();
        await Parallel.ForEachAsync(filePaths,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Environment.ProcessorCount,
                CancellationToken = cancellationToken
            }, async (filePath, token) =>
            {
                try
                {
                    string text = await File.ReadAllTextAsync(filePath, token).ConfigureAwait(false);
                    string relativePath = Path.GetRelativePath(rootPath, filePath).Replace('\\', '/');
                    SourceFile sourceFile = new SourceFile(relativePath, "yaml", [], [], text);
                    YamlParser.YamlParseResult doc = YamlParser.Parse(text);
                    ExecutionStatistics.RecordInputFile(filePath);
                    parsed.Add((sourceFile, doc));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new IOException(
                        $"Cannot read YAML file '{filePath}': {ex.Message}", ex);
                }
            }).ConfigureAwait(false);

        List<(SourceFile File, YamlParser.YamlParseResult Doc)> sorted = parsed.OrderBy(p => p.File.Path, StringComparer.Ordinal).ToList();

        List<object> entries = new List<object>();
        List<object> documents = new List<object>();
        foreach ((SourceFile? file, YamlParser.YamlParseResult? doc) in sorted)
        {
            entries.AddRange(doc.Entries.Select(e => (object)(e with { File = file })));
            documents.AddRange(doc.Documents.Select(d => (object)(d with { File = file })));
        }

        Dictionary<string, List<object>> collections = new Dictionary<string, List<object>>(StringComparer.Ordinal);
        string? requested = query.Collection;
        if (requested is null || requested == "Entries")
        {
            collections["Entries"] = entries;
        }

        if (requested is null || requested == "Documents")
        {
            collections["Documents"] = documents;
        }

        return ApplyQueryFilters(query, collections);
    }

    private static void CollectYamlFiles(string dir, IReadOnlySet<string>? excluded, List<string> result)
    {
        try
        {
            foreach (string file in Directory.GetFiles(dir))
            {
                if (s_yamlExtensions.Contains(Path.GetExtension(file)))
                {
                    result.Add(file);
                }
            }
            foreach (string subDir in Directory.GetDirectories(dir))
            {
                string dirName = Path.GetFileName(subDir);
                if (excluded is not null && excluded.Contains(dirName))
                {
                    continue;
                }

                CollectYamlFiles(subDir, excluded, result);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException(
                $"Cannot enumerate YAML files in '{dir}': {ex.Message}", ex);
        }
    }

    private static Dictionary<string, Dictionary<string, Func<object, object?>>> BuildAccessors()
    {
        return new()
        {
            ["YamlEntry"] = new()
            {
                ["Path"] = o => ((YamlEntryInfo)o).Path,
                ["Key"] = o => ((YamlEntryInfo)o).Key,
                ["Value"] = o => ((YamlEntryInfo)o).Value,
                ["Line"] = o => (object)((YamlEntryInfo)o).Line,
                ["Document"] = o => (object)((YamlEntryInfo)o).Document,
                ["File"] = o => ((YamlEntryInfo)o).File,
                ["Source"] = o => ((YamlEntryInfo)o).Source,
            },
            ["YamlDocument"] = new()
            {
                ["Index"] = o => (object)((YamlDocumentInfo)o).Index,
                ["Line"] = o => (object)((YamlDocumentInfo)o).Line,
                ["File"] = o => ((YamlDocumentInfo)o).File,
                ["Source"] = o => ((YamlDocumentInfo)o).Source,
            },
            ["File"] = new()
            {
                ["Path"] = o => ((SourceFile)o).Path,
                ["Language"] = o => ((SourceFile)o).Language,
                ["Namespace"] = o => ((SourceFile)o).Namespace,
                ["Usings"] = o => (object)((SourceFile)o).Usings,
                ["Types"] = o => (object)((SourceFile)o).Types,
                ["Projects"] = o => (object)((SourceFile)o).Projects,
            },
        };
    }
}
