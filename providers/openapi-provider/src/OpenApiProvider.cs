// Exposes OpenAPI paths and operations as queryable provider collections.

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
    /// <inheritdoc />
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

    /// <inheritdoc />
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

    /// <inheritdoc />
    public override object? Query(ProviderQuery query)
    {
        if (query.RootPath is null)
        {
            return new Dictionary<string, List<object>>();
        }

        string rootPath = query.RootPath;
        IReadOnlySet<string>? excluded = query.ExcludedDirectories;

        List<string> filePaths = new List<string>();
        CollectOpenApiCandidateFiles(rootPath, excluded, filePaths);

        ConcurrentBag<(SourceFile File, OpenApiParser.OpenApiParseResult Spec)> parsed = new ConcurrentBag<(SourceFile File, OpenApiParser.OpenApiParseResult Spec)>();
        Parallel.ForEach(filePaths,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            filePath =>
            {
                try
                {
                    string text = File.ReadAllText(filePath);
                    string extension = Path.GetExtension(filePath);
                    OpenApiParser.OpenApiParseResult spec = extension.Equals(".json", StringComparison.OrdinalIgnoreCase)
                        ? OpenApiParser.ParseJson(text)
                        : OpenApiParser.ParseYaml(text);
                    ExecutionStatistics.RecordInputFile(filePath);

                    if (spec.Operations.Count == 0 && spec.Paths.Count == 0)
                    {
                        return;
                    }

                    string relativePath = Path.GetRelativePath(rootPath, filePath).Replace('\\', '/');
                    SourceFile sourceFile = new SourceFile(relativePath, "openapi", [], [], text);
                    parsed.Add((sourceFile, spec));
                }
                catch { }
            });

        List<(SourceFile File, OpenApiParser.OpenApiParseResult Spec)> sorted = parsed.OrderBy(p => p.File.Path, StringComparer.Ordinal).ToList();

        List<object> operations = new List<object>();
        List<object> paths = new List<object>();
        foreach ((SourceFile? file, OpenApiParser.OpenApiParseResult? spec) in sorted)
        {
            operations.AddRange(spec.Operations.Select(o => (object)(o with { File = file })));
            paths.AddRange(spec.Paths.Select(p => (object)(p with { File = file })));
        }

        Dictionary<string, List<object>> collections = new Dictionary<string, List<object>>(StringComparer.Ordinal);
        string? requested = query.Collection;
        if (requested is null || requested == "Operations")
        {
            collections["Operations"] = operations;
        }

        if (requested is null || requested == "Paths")
        {
            collections["Paths"] = paths;
        }

        return ApplyQueryFilters(query, collections);
    }

    private static void CollectOpenApiCandidateFiles(string dir, IReadOnlySet<string>? excluded, List<string> result)
    {
        try
        {
            foreach (string file in Directory.GetFiles(dir))
            {
                if (OpenApiExtensions.Contains(Path.GetExtension(file)))
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
