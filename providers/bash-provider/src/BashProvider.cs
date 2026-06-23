using System.Collections.Concurrent;
using Cop.Core;
using Cop.Providers.SourceModel;

namespace Cop.Providers.Bash;

/// <summary>
/// Provider for Bash/Shell script analysis. Scans .sh/.bash files, parses simple
/// commands and per-script strict-mode metadata, and returns flat CLR collections.
/// </summary>
public sealed class BashProvider : DataProvider
{
    public override ReadOnlyMemory<byte> GetSchema() => _schema.ToJson();

    private static readonly ProviderSchema _schema = BuildSchema();

    private static readonly HashSet<string> BashExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".sh", ".bash" };

    private static ProviderSchema BuildSchema()
    {
        return new ProviderSchema
        {
            Types =
            [
                TypeDef("ShellCommand", null,
                    Prop("Name"), Prop("Text"), Prop("Line", "int"),
                    Opt("File", "File"), Prop("Source")),

                TypeDef("ShellScript", null,
                    Prop("HasStrictMode", "bool"), Prop("Line", "int"),
                    Opt("File", "File"), Prop("Source")),
            ],
            Collections =
            [
                new() { Name = "Commands", ItemType = "ShellCommand" },
                new() { Name = "Scripts", ItemType = "ShellScript" },
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
                [typeof(ShellCommandInfo)] = "ShellCommand",
                [typeof(ShellScriptInfo)] = "ShellScript",
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
        CollectBashFiles(rootPath, excluded, filePaths);

        var parsed = new ConcurrentBag<(SourceFile File, BashParser.BashParseResult Script)>();
        Parallel.ForEach(filePaths,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            filePath =>
            {
                try
                {
                    var text = File.ReadAllText(filePath);
                    var relativePath = Path.GetRelativePath(rootPath, filePath).Replace('\\', '/');
                    var sourceFile = new SourceFile(relativePath, "bash", [], [], text);
                    var script = BashParser.Parse(text);
                    parsed.Add((sourceFile, script));
                }
                catch { }
            });

        var sorted = parsed.OrderBy(p => p.File.Path, StringComparer.Ordinal).ToList();

        var commands = new List<object>();
        var scripts = new List<object>();
        foreach (var (file, script) in sorted)
        {
            commands.AddRange(script.Commands.Select(c => (object)(c with { File = file })));
            scripts.Add(script.Script with { File = file });
        }

        var collections = new Dictionary<string, List<object>>(StringComparer.Ordinal);
        var requested = query.Collection;
        if (requested is null || requested == "Commands") collections["Commands"] = commands;
        if (requested is null || requested == "Scripts") collections["Scripts"] = scripts;
        return collections;
    }

    private static void CollectBashFiles(string dir, IReadOnlySet<string>? excluded, List<string> result)
    {
        try
        {
            foreach (var file in Directory.GetFiles(dir))
            {
                if (BashExtensions.Contains(Path.GetExtension(file)))
                    result.Add(file);
            }
            foreach (var subDir in Directory.GetDirectories(dir))
            {
                var dirName = Path.GetFileName(subDir);
                if (excluded is not null && excluded.Contains(dirName)) continue;
                CollectBashFiles(subDir, excluded, result);
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
    }

    private static Dictionary<string, Dictionary<string, Func<object, object?>>> BuildAccessors()
    {
        return new()
        {
            ["ShellCommand"] = new()
            {
                ["Name"] = o => ((ShellCommandInfo)o).Name,
                ["Text"] = o => ((ShellCommandInfo)o).Text,
                ["Line"] = o => (object)((ShellCommandInfo)o).Line,
                ["File"] = o => ((ShellCommandInfo)o).File,
                ["Source"] = o => ((ShellCommandInfo)o).Source,
            },
            ["ShellScript"] = new()
            {
                ["HasStrictMode"] = o => (object)((ShellScriptInfo)o).HasStrictMode,
                ["Line"] = o => (object)((ShellScriptInfo)o).Line,
                ["File"] = o => ((ShellScriptInfo)o).File,
                ["Source"] = o => ((ShellScriptInfo)o).Source,
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

