using System.Collections.Concurrent;
using Cop.Core;
using Cop.Providers.SourceModel;

namespace Cop.Providers.OpenApi;

/// <summary>
/// Provider for OpenAPI/Swagger analysis. Scans YAML, YML, and JSON files, keeps only files
/// with a top-level OpenAPI marker, and returns paths and operations for static checks.
/// </summary>
public sealed class OpenApiProvider : DataProvider
{
    public override ReadOnlyMemory<byte> GetSchema() => _schema.ToJson();

    private static readonly ProviderSchema _schema = BuildSchema();

    private static readonly HashSet<string> OpenApiExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".yaml", ".yml", ".json" };

    private static ProviderSchema BuildSchema()
    {
        return new ProviderSchema
        {
            Types =
            [
                TypeDef("OpenApiOperation", null,
                    Prop("Method"), Prop("Path"), Prop("OperationId"),
                    Prop("HasSummary", "bool"), Prop("HasResponses", "bool"),
                    Prop("Line", "int"), Opt("File", "File"), Prop("Source")),

                TypeDef("OpenApiPath", null,
                    Prop("Path"), Prop("Line", "int"), Opt("File", "File"), Prop("Source")),
            ],
            Collections =
            [
                new() { Name = "Operations", ItemType = "OpenApiOperation" },
                new() { Name = "Paths", ItemType = "OpenApiPath" },
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
                [typeof(OpenApiOperationInfo)] = "OpenApiOperation",
                [typeof(OpenApiPathInfo)] = "OpenApiPath",
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
        CollectOpenApiCandidateFiles(rootPath, excluded, filePaths);

        var parsed = new ConcurrentBag<(SourceFile File, OpenApiParser.OpenApiParseResult Spec)>();
        Parallel.ForEach(filePaths,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            filePath =>
            {
                try
                {
                    var text = File.ReadAllText(filePath);
                    var extension = Path.GetExtension(filePath);
                    var spec = extension.Equals(".json", StringComparison.OrdinalIgnoreCase)
                        ? OpenApiParser.ParseJson(text)
                        : OpenApiParser.ParseYaml(text);

                    if (spec.Operations.Count == 0 && spec.Paths.Count == 0)
                        return;

                    var relativePath = Path.GetRelativePath(rootPath, filePath).Replace('\\', '/');
                    var sourceFile = new SourceFile(relativePath, "openapi", [], [], text);
                    parsed.Add((sourceFile, spec));
                }
                catch { }
            });

        var sorted = parsed.OrderBy(p => p.File.Path, StringComparer.Ordinal).ToList();

        var operations = new List<object>();
        var paths = new List<object>();
        foreach (var (file, spec) in sorted)
        {
            operations.AddRange(spec.Operations.Select(o => (object)(o with { File = file })));
            paths.AddRange(spec.Paths.Select(p => (object)(p with { File = file })));
        }

        var collections = new Dictionary<string, List<object>>(StringComparer.Ordinal);
        var requested = query.Collection;
        if (requested is null || requested == "Operations") collections["Operations"] = operations;
        if (requested is null || requested == "Paths") collections["Paths"] = paths;
        return collections;
    }

    private static void CollectOpenApiCandidateFiles(string dir, IReadOnlySet<string>? excluded, List<string> result)
    {
        try
        {
            foreach (var file in Directory.GetFiles(dir))
            {
                if (OpenApiExtensions.Contains(Path.GetExtension(file)))
                    result.Add(file);
            }
            foreach (var subDir in Directory.GetDirectories(dir))
            {
                var dirName = Path.GetFileName(subDir);
                if (excluded is not null && excluded.Contains(dirName)) continue;
                CollectOpenApiCandidateFiles(subDir, excluded, result);
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
    }

    private static Dictionary<string, Dictionary<string, Func<object, object?>>> BuildAccessors()
    {
        return new()
        {
            ["OpenApiOperation"] = new()
            {
                ["Method"] = o => ((OpenApiOperationInfo)o).Method,
                ["Path"] = o => ((OpenApiOperationInfo)o).Path,
                ["OperationId"] = o => ((OpenApiOperationInfo)o).OperationId,
                ["HasSummary"] = o => (object)((OpenApiOperationInfo)o).HasSummary,
                ["HasResponses"] = o => (object)((OpenApiOperationInfo)o).HasResponses,
                ["Line"] = o => (object)((OpenApiOperationInfo)o).Line,
                ["File"] = o => ((OpenApiOperationInfo)o).File,
                ["Source"] = o => ((OpenApiOperationInfo)o).Source,
            },
            ["OpenApiPath"] = new()
            {
                ["Path"] = o => ((OpenApiPathInfo)o).Path,
                ["Line"] = o => (object)((OpenApiPathInfo)o).Line,
                ["File"] = o => ((OpenApiPathInfo)o).File,
                ["Source"] = o => ((OpenApiPathInfo)o).Source,
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

