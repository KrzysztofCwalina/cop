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
    public override ReadOnlyMemory<byte> GetSchema() => _schema.ToJson();

    private static readonly ProviderSchema _schema = BuildSchema();

    private static readonly HashSet<string> YamlExtensions = new(StringComparer.OrdinalIgnoreCase)
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
        => new() { Name = name, Type = type, Optional = true };

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

    public override object? Query(ProviderQuery query)
    {
        if (query.RootPath is null)
            return new Dictionary<string, List<object>>();

        var rootPath = query.RootPath;
        var excluded = query.ExcludedDirectories;

        var filePaths = new List<string>();
        CollectYamlFiles(rootPath, excluded, filePaths);

        var parsed = new ConcurrentBag<(SourceFile File, YamlParser.YamlParseResult Doc)>();
        Parallel.ForEach(filePaths,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            filePath =>
            {
                try
                {
                    var text = File.ReadAllText(filePath);
                    var relativePath = Path.GetRelativePath(rootPath, filePath).Replace('\\', '/');
                    var sourceFile = new SourceFile(relativePath, "yaml", [], [], text);
                    var doc = YamlParser.Parse(text);
                    parsed.Add((sourceFile, doc));
                }
                catch { }
            });

        var sorted = parsed.OrderBy(p => p.File.Path, StringComparer.Ordinal).ToList();

        var entries = new List<object>();
        var documents = new List<object>();
        foreach (var (file, doc) in sorted)
        {
            entries.AddRange(doc.Entries.Select(e => (object)(e with { File = file })));
            documents.AddRange(doc.Documents.Select(d => (object)(d with { File = file })));
        }

        var collections = new Dictionary<string, List<object>>(StringComparer.Ordinal);
        var requested = query.Collection;
        if (requested is null || requested == "Entries") collections["Entries"] = entries;
        if (requested is null || requested == "Documents") collections["Documents"] = documents;
        return collections;
    }

    private static void CollectYamlFiles(string dir, IReadOnlySet<string>? excluded, List<string> result)
    {
        try
        {
            foreach (var file in Directory.GetFiles(dir))
            {
                if (YamlExtensions.Contains(Path.GetExtension(file)))
                    result.Add(file);
            }
            foreach (var subDir in Directory.GetDirectories(dir))
            {
                var dirName = Path.GetFileName(subDir);
                if (excluded is not null && excluded.Contains(dirName)) continue;
                CollectYamlFiles(subDir, excluded, result);
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
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
