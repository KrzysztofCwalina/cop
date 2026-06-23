using System.Collections.Concurrent;
using Cop.Core;
using Cop.Providers.SourceModel;

namespace Cop.Providers.PowerShell;

/// <summary>
/// Provider for PowerShell script analysis. Scans .ps1/.psm1/.psd1 files, parses simple
/// commands and per-script strict-mode metadata, and returns flat CLR collections.
/// </summary>
public sealed class PowerShellProvider : DataProvider
{
    public override ReadOnlyMemory<byte> GetSchema() => _schema.ToJson();

    private static readonly ProviderSchema _schema = BuildSchema();

    private static readonly HashSet<string> PowerShellExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".ps1", ".psm1", ".psd1" };

    private static ProviderSchema BuildSchema()
    {
        return new ProviderSchema
        {
            Types =
            [
                TypeDef("PowerShellCommand", null,
                    Prop("Name"), Prop("Text"), Prop("Line", "int"),
                    Opt("File", "File"), Prop("Source")),

                TypeDef("PowerShellScript", null,
                    Prop("UsesStrictMode", "bool"), Prop("Line", "int"),
                    Opt("File", "File"), Prop("Source")),
            ],
            Collections =
            [
                new() { Name = "Commands", ItemType = "PowerShellCommand" },
                new() { Name = "Scripts", ItemType = "PowerShellScript" },
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
                [typeof(PowerShellCommandInfo)] = "PowerShellCommand",
                [typeof(PowerShellScriptInfo)] = "PowerShellScript",
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
        CollectPowerShellFiles(rootPath, excluded, filePaths);

        var parsed = new ConcurrentBag<(SourceFile File, PowerShellParser.PowerShellParseResult Script)>();
        Parallel.ForEach(filePaths,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            filePath =>
            {
                try
                {
                    var text = File.ReadAllText(filePath);
                    var relativePath = Path.GetRelativePath(rootPath, filePath).Replace('\\', '/');
                    var sourceFile = new SourceFile(relativePath, "powershell", [], [], text);
                    var script = PowerShellParser.Parse(text);
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

    private static void CollectPowerShellFiles(string dir, IReadOnlySet<string>? excluded, List<string> result)
    {
        try
        {
            foreach (var file in Directory.GetFiles(dir))
            {
                if (PowerShellExtensions.Contains(Path.GetExtension(file)))
                    result.Add(file);
            }
            foreach (var subDir in Directory.GetDirectories(dir))
            {
                var dirName = Path.GetFileName(subDir);
                if (excluded is not null && excluded.Contains(dirName)) continue;
                CollectPowerShellFiles(subDir, excluded, result);
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
    }

    private static Dictionary<string, Dictionary<string, Func<object, object?>>> BuildAccessors()
    {
        return new()
        {
            ["PowerShellCommand"] = new()
            {
                ["Name"] = o => ((PowerShellCommandInfo)o).Name,
                ["Text"] = o => ((PowerShellCommandInfo)o).Text,
                ["Line"] = o => (object)((PowerShellCommandInfo)o).Line,
                ["File"] = o => ((PowerShellCommandInfo)o).File,
                ["Source"] = o => ((PowerShellCommandInfo)o).Source,
            },
            ["PowerShellScript"] = new()
            {
                ["UsesStrictMode"] = o => (object)((PowerShellScriptInfo)o).UsesStrictMode,
                ["Line"] = o => (object)((PowerShellScriptInfo)o).Line,
                ["File"] = o => ((PowerShellScriptInfo)o).File,
                ["Source"] = o => ((PowerShellScriptInfo)o).Source,
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
