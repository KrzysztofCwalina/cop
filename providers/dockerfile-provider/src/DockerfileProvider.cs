// Exposes Dockerfile instructions and build stages as queryable provider data.

using Cop.Core;
using Cop.Providers.SourceModel;

namespace Cop.Providers.Dockerfile;

/// <summary>
/// Provider for Dockerfile analysis. Scans Dockerfile, Dockerfile.*, and *.dockerfile files,
/// parses instructions and build stages, and returns flat CLR object collections.
/// </summary>
public sealed class DockerfileProvider : DataProvider
{
    /// <inheritdoc />
    public override ReadOnlyMemory<byte> GetSchema() => s_schema.ToJson();

    private static readonly ProviderSchema s_schema = BuildSchema();

    private static ProviderSchema BuildSchema()
    {
        return new ProviderSchema
        {
            Types =
            [
                TypeDef("DockerInstruction", null,
                    Prop("Instruction"), Prop("Argument"),
                    Prop("Line", "int"), Prop("Stage", "int"),
                    Prop("IsJsonForm", "bool"), new() { Name = "Arguments", Type = "string", IsCollection = true },
                    Opt("From"), Prop("ReferencedStage", "int"),
                    Opt("File", "File"), Prop("Source")),

                TypeDef("DockerStage", null,
                    Prop("Name"), Prop("Image"),
                    Prop("Index", "int"), Prop("Line", "int"),
                    Opt("File", "File"), Prop("Source")),
            ],
            Collections =
            [
                new() { Name = "Instructions", ItemType = "DockerInstruction" },
                new() { Name = "Stages", ItemType = "DockerStage" },
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
                [typeof(DockerInstructionInfo)] = "DockerInstruction",
                [typeof(DockerStageInfo)] = "DockerStage",
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
        DeferredProviderResult result = (DeferredProviderResult)
            (await QueryDeferredAsync(query, cancellationToken).ConfigureAwait(false))!;
        return await ProviderCollections.MaterializeAsync(result, cancellationToken)
            .ConfigureAwait(false);
    }

    private readonly Dictionary<string, DeferredFormatSnapshot> _snapshots = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public override ValueTask<object?> QueryDeferredAsync(
        ProviderQuery query, CancellationToken cancellationToken = default)
    {
        if (query.RootPath is null)
        {
            return ValueTask.FromResult<object?>(new DeferredProviderResult(
                new Dictionary<string, IAsyncEnumerable<object>>()));
        }

        string key = DeferredFormatSnapshot.Key(query);
        string root = Path.GetFullPath(query.RootPath);
        if (!_snapshots.TryGetValue(key, out DeferredFormatSnapshot? snapshot))
        {
            snapshot = new(() =>
            {
                List<string> paths = new List<string>();
                CollectDockerfiles(root, query.ExcludedDirectories, paths);
                return paths;
            }, async (path, token) =>
            {
                string text = await File.ReadAllTextAsync(path, token).ConfigureAwait(false);
                SourceFile file = new SourceFile(Path.GetRelativePath(root, path).Replace('\\', '/'), "dockerfile", [], [], text);
                DockerfileParser.DockerfileParseResult doc = DockerfileParser.Parse(text);
                return new()
                {
                    ["Instructions"] = doc.Instructions.Select(i => (object)(i with { File = file })).ToList(),
                    ["Stages"] = doc.Stages.Select(s => (object)(s with { File = file })).ToList()
                };
            });
            _snapshots.Add(key, snapshot);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<object?>(snapshot.Query(query, s_schema, GetRuntimeBindings()));
    }

    private static void CollectDockerfiles(string dir, IReadOnlySet<string>? excluded, List<string> result)
    {
        try
        {
            foreach (string file in Directory.GetFiles(dir))
            {
                if (IsDockerfile(file))
                {
                    result.Add(file);
                }
            }
            foreach (string subDir in Directory.GetDirectories(dir))
            {
                if ((File.GetAttributes(subDir) & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                string dirName = Path.GetFileName(subDir);
                if (excluded is not null && excluded.Contains(dirName))
                {
                    continue;
                }

                CollectDockerfiles(subDir, excluded, result);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            ProviderErrors.Report($"Failed to scan '{dir}': {ex.Message}");
        }
    }

    private static bool IsDockerfile(string file)
    {
        string name = Path.GetFileName(file);
        return name.Equals("Dockerfile", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Dockerfile.", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".dockerfile", StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, Dictionary<string, Func<object, object?>>> BuildAccessors()
    {
        return new()
        {
            ["DockerInstruction"] = new()
            {
                ["Instruction"] = o => ((DockerInstructionInfo)o).Instruction,
                ["Argument"] = o => ((DockerInstructionInfo)o).Argument,
                ["Line"] = o => (object)((DockerInstructionInfo)o).Line,
                ["Stage"] = o => (object)((DockerInstructionInfo)o).Stage,
                ["IsJsonForm"] = o => ((DockerInstructionInfo)o).IsJsonForm,
                ["Arguments"] = o => ((DockerInstructionInfo)o).Arguments,
                ["From"] = o => ((DockerInstructionInfo)o).From,
                ["ReferencedStage"] = o => ((DockerInstructionInfo)o).ReferencedStage,
                ["File"] = o => ((DockerInstructionInfo)o).File,
                ["Source"] = o => ((DockerInstructionInfo)o).Source,
            },
            ["DockerStage"] = new()
            {
                ["Name"] = o => ((DockerStageInfo)o).Name,
                ["Image"] = o => ((DockerStageInfo)o).Image,
                ["Index"] = o => (object)((DockerStageInfo)o).Index,
                ["Line"] = o => (object)((DockerStageInfo)o).Line,
                ["File"] = o => ((DockerStageInfo)o).File,
                ["Source"] = o => ((DockerStageInfo)o).Source,
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
