using System.Collections.Concurrent;
using Cop.Core;
using Cop.Providers.SourceModel;

namespace Cop.Providers.Dockerfile;

/// <summary>
/// Provider for Dockerfile analysis. Scans Dockerfile, Dockerfile.*, and *.dockerfile files,
/// parses instructions and build stages, and returns flat CLR object collections.
/// </summary>
public sealed class DockerfileProvider : DataProvider
{
    public override ReadOnlyMemory<byte> GetSchema() => _schema.ToJson();

    private static readonly ProviderSchema _schema = BuildSchema();

    private static ProviderSchema BuildSchema()
    {
        return new ProviderSchema
        {
            Types =
            [
                TypeDef("DockerInstruction", null,
                    Prop("Instruction"), Prop("Argument"),
                    Prop("Line", "int"), Prop("Stage", "int"),
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
        => new() { Name = name, Type = type, Optional = true };

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

    public override object? Query(ProviderQuery query)
    {
        if (query.RootPath is null)
            return new Dictionary<string, List<object>>();

        var rootPath = query.RootPath;
        var excluded = query.ExcludedDirectories;

        var filePaths = new List<string>();
        CollectDockerfiles(rootPath, excluded, filePaths);

        var parsed = new ConcurrentBag<(SourceFile File, DockerfileParser.DockerfileParseResult Doc)>();
        Parallel.ForEach(filePaths,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            filePath =>
            {
                try
                {
                    var text = File.ReadAllText(filePath);
                    var relativePath = Path.GetRelativePath(rootPath, filePath).Replace('\\', '/');
                    var sourceFile = new SourceFile(relativePath, "dockerfile", [], [], text);
                    var doc = DockerfileParser.Parse(text);
                    parsed.Add((sourceFile, doc));
                }
                catch { }
            });

        var sorted = parsed.OrderBy(p => p.File.Path, StringComparer.Ordinal).ToList();

        var instructions = new List<object>();
        var stages = new List<object>();
        foreach (var (file, doc) in sorted)
        {
            instructions.AddRange(doc.Instructions.Select(i => (object)(i with { File = file })));
            stages.AddRange(doc.Stages.Select(s => (object)(s with { File = file })));
        }

        var collections = new Dictionary<string, List<object>>(StringComparer.Ordinal);
        var requested = query.Collection;
        if (requested is null || requested == "Instructions") collections["Instructions"] = instructions;
        if (requested is null || requested == "Stages") collections["Stages"] = stages;
        return collections;
    }

    private static void CollectDockerfiles(string dir, IReadOnlySet<string>? excluded, List<string> result)
    {
        try
        {
            foreach (var file in Directory.GetFiles(dir))
            {
                if (IsDockerfile(file))
                    result.Add(file);
            }
            foreach (var subDir in Directory.GetDirectories(dir))
            {
                var dirName = Path.GetFileName(subDir);
                if (excluded is not null && excluded.Contains(dirName)) continue;
                CollectDockerfiles(subDir, excluded, result);
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
    }

    private static bool IsDockerfile(string file)
    {
        var name = Path.GetFileName(file);
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
