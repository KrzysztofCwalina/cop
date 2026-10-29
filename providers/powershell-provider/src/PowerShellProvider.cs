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
        var result = (DeferredProviderResult)QueryDeferred(query);
        return result.Collections.ToDictionary(c => c.Key, c => c.Value.ToList(), StringComparer.Ordinal);
    }

    private readonly Dictionary<string, DeferredFormatSnapshot> _snapshots = new(StringComparer.Ordinal);
    private readonly PowerShellAstParser _parser = new();

    public override object QueryDeferred(ProviderQuery query)
    {
        if (query.RootPath is null) return new DeferredProviderResult(new Dictionary<string, IEnumerable<object>>());
        var key = DeferredFormatSnapshot.Key(query);
        var root = Path.GetFullPath(query.RootPath);
        if (!_snapshots.TryGetValue(key, out var snapshot))
        {
            snapshot = new(() =>
            {
                var paths = new List<string>();
                CollectPowerShellFiles(root, query.ExcludedDirectories, paths);
                return paths;
            }, path =>
            {
                var text = File.ReadAllText(path);
                var file = new SourceFile(Path.GetRelativePath(root, path).Replace('\\', '/'), "powershell", [], [], text);
                var script = _parser.Parse(text, path);
                return new()
                {
                    ["Commands"] = script.Commands.Select(c => (object)(c with { File = file })).ToList(),
                    ["Scripts"] = [script.Script with { File = file }]
                };
            });
            _snapshots.Add(key, snapshot);
        }
        return snapshot.Query(query, _schema, GetRuntimeBindings());
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
                if ((File.GetAttributes(subDir) & FileAttributes.ReparsePoint) != 0) continue;
                var dirName = Path.GetFileName(subDir);
                if (excluded is not null && excluded.Contains(dirName)) continue;
                CollectPowerShellFiles(subDir, excluded, result);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            ProviderErrors.Report($"Failed to scan '{dir}': {ex.Message}");
        }
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
