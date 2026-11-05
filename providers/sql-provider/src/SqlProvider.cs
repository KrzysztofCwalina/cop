// Exposes SQL statement facts and deferred parsing through provider queries.

using Cop.Core;
using Cop.Providers.SourceModel;

namespace Cop.Providers.Sql;

/// <summary>
/// Provider for SQL file analysis. Scans .sql files, splits them into statements, and
/// returns statement metadata useful for deterministic static analysis.
/// </summary>
public sealed class SqlProvider : DataProvider
{
    /// <inheritdoc />
    public override ReadOnlyMemory<byte> GetSchema() => _schema.ToJson();
    /// <inheritdoc />
    public override string QueryResultType => "SqlContent";

    private static readonly ProviderSchema _schema = BuildSchema();

    private static readonly HashSet<string> SqlExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".sql" };

    private static ProviderSchema BuildSchema()
    {
        return new ProviderSchema
        {
            Types =
            [
                TypeDef("SqlStatement", null,
                    Prop("Kind"), Prop("Text"),
                    Prop("Line", "int"), Prop("SelectsStar", "bool"), Prop("HasWhere", "bool"),
                    Opt("File", "File"), Prop("Source")),
            ],
            Collections =
            [
                new() { Name = "Statements", ItemType = "SqlStatement" },
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
                [typeof(SqlStatementInfo)] = "SqlStatement",
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
        DeferredProviderResult result = (DeferredProviderResult)QueryDeferred(query);
        return result.Collections.ToDictionary(c => c.Key, c => c.Value.ToList(), StringComparer.Ordinal);
    }

    private readonly Dictionary<string, DeferredFormatSnapshot> _snapshots = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public override object QueryDeferred(ProviderQuery query)
    {
        if (query.RootPath is null)
        {
            return new DeferredProviderResult(new Dictionary<string, IEnumerable<object>>());
        }

        string key = DeferredFormatSnapshot.Key(query);
        string root = Path.GetFullPath(query.RootPath);
        if (!_snapshots.TryGetValue(key, out DeferredFormatSnapshot? snapshot))
        {
            snapshot = new(() =>
            {
                List<string> paths = new List<string>();
                CollectSqlFiles(root, query.ExcludedDirectories, paths);
                return paths;
            }, path =>
            {
                string text = File.ReadAllText(path);
                SourceFile file = new SourceFile(Path.GetRelativePath(root, path).Replace('\\', '/'), "sql", [], [], text);
                SqlParser.SqlParseResult doc = SqlParser.Parse(text);
                return new() { ["Statements"] = doc.Statements.Select(s => (object)(s with { File = file })).ToList() };
            });
            _snapshots.Add(key, snapshot);
        }
        return snapshot.Query(query, _schema, GetRuntimeBindings());
    }

    private static void CollectSqlFiles(string dir, IReadOnlySet<string>? excluded, List<string> result)
    {
        try
        {
            foreach (string file in Directory.GetFiles(dir))
            {
                if (SqlExtensions.Contains(Path.GetExtension(file)))
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

                CollectSqlFiles(subDir, excluded, result);
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
            ["SqlStatement"] = new()
            {
                ["Kind"] = o => ((SqlStatementInfo)o).Kind,
                ["Text"] = o => ((SqlStatementInfo)o).Text,
                ["Line"] = o => (object)((SqlStatementInfo)o).Line,
                ["SelectsStar"] = o => (object)((SqlStatementInfo)o).SelectsStar,
                ["HasWhere"] = o => (object)((SqlStatementInfo)o).HasWhere,
                ["File"] = o => ((SqlStatementInfo)o).File,
                ["Source"] = o => ((SqlStatementInfo)o).Source,
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
