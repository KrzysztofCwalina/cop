// Exposes PowerShell scripts and commands through provider runtime bindings.

using Cop.Core;
using Cop.Providers.SourceModel;

namespace Cop.Providers.PowerShell;

/// <summary>
/// Provider for PowerShell script analysis. Scans .ps1/.psm1/.psd1 files, parses simple
/// commands and per-script strict-mode metadata, and returns flat CLR collections.
/// </summary>
public sealed class PowerShellProvider : DataProvider
{
    /// <inheritdoc />
    public override ReadOnlyMemory<byte> GetSchema() => s_schema.ToJson();

    private static readonly ProviderSchema s_schema = BuildSchema();

    private static readonly HashSet<string> s_powerShellExtensions = new(StringComparer.OrdinalIgnoreCase)
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
        => new() { Name = name, Type = type, IsOptional = true };

    /// <inheritdoc />
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
    private readonly PowerShellAstParser _parser = new();

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
                CollectPowerShellFiles(root, query.ExcludedDirectories, paths);
                return paths;
            }, async (path, token) =>
            {
                string text = await File.ReadAllTextAsync(path, token).ConfigureAwait(false);
                SourceFile file = new SourceFile(Path.GetRelativePath(root, path).Replace('\\', '/'), "powershell", [], [], text);
                PowerShellParser.PowerShellParseResult script =
                    await _parser.ParseAsync(text, path, token).ConfigureAwait(false);
                return new()
                {
                    ["Commands"] = script.Commands.Select(c => (object)(c with { File = file })).ToList(),
                    ["Scripts"] = [script.Script with { File = file }]
                };
            });
            _snapshots.Add(key, snapshot);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<object?>(snapshot.Query(query, s_schema, GetRuntimeBindings()));
    }

    private static void CollectPowerShellFiles(string dir, IReadOnlySet<string>? excluded, List<string> result)
    {
        try
        {
            foreach (string file in Directory.GetFiles(dir))
            {
                if (s_powerShellExtensions.Contains(Path.GetExtension(file)))
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
