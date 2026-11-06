// Exposes parsed Bash scripts and commands through the provider query contract.

using Cop.Core;
using Cop.Providers.SourceModel;

namespace Cop.Providers.Bash;

/// <summary>
/// Provider for Bash/Shell script analysis. Scans .sh/.bash files, parses simple
/// commands and per-script strict-mode metadata, and returns flat CLR collections.
/// </summary>
public sealed class BashProvider : DataProvider
{
    /// <inheritdoc />
    public override ReadOnlyMemory<byte> GetSchema() => s_schema.ToJson();

    private static readonly ProviderSchema s_schema = BuildSchema();

    private static readonly HashSet<string> s_bashExtensions = new(StringComparer.OrdinalIgnoreCase)
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
        => new() { Name = name, Type = type, IsOptional = true };

    /// <inheritdoc />
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
                CollectBashFiles(root, query.ExcludedDirectories, paths);
                return paths;
            }, async (path, token) =>
            {
                string text = await File.ReadAllTextAsync(path, token).ConfigureAwait(false);
                SourceFile file = new SourceFile(Path.GetRelativePath(root, path).Replace('\\', '/'), "bash", [], [], text);
                BashParser.BashParseResult script = BashParser.Parse(text);
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

    private static void CollectBashFiles(string dir, IReadOnlySet<string>? excluded, List<string> result)
    {
        try
        {
            foreach (string file in Directory.GetFiles(dir))
            {
                if (s_bashExtensions.Contains(Path.GetExtension(file)))
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

                CollectBashFiles(subDir, excluded, result);
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
