using System.Collections.Concurrent;
using Cop.Core;
using Cop.Providers.SourceModel;

namespace Cop.Providers.Sql;

/// <summary>
/// Provider for SQL file analysis. Scans .sql files, splits them into statements, and
/// returns statement metadata useful for deterministic static analysis.
/// </summary>
public sealed class SqlProvider : DataProvider
{
    public override ReadOnlyMemory<byte> GetSchema() => _schema.ToJson();

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

    public override object? Query(ProviderQuery query)
    {
        if (query.RootPath is null)
            return new Dictionary<string, List<object>>();

        var rootPath = query.RootPath;
        var excluded = query.ExcludedDirectories;

        var filePaths = new List<string>();
        CollectSqlFiles(rootPath, excluded, filePaths);

        var parsed = new ConcurrentBag<(SourceFile File, SqlParser.SqlParseResult Doc)>();
        Parallel.ForEach(filePaths,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            filePath =>
            {
                try
                {
                    var text = File.ReadAllText(filePath);
                    var relativePath = Path.GetRelativePath(rootPath, filePath).Replace('\\', '/');
                    var sourceFile = new SourceFile(relativePath, "sql", [], [], text);
                    var doc = SqlParser.Parse(text);
                    parsed.Add((sourceFile, doc));
                }
                catch { }
            });

        var sorted = parsed.OrderBy(p => p.File.Path, StringComparer.Ordinal).ToList();

        var statements = new List<object>();
        foreach (var (file, doc) in sorted)
        {
            statements.AddRange(doc.Statements.Select(s => (object)(s with { File = file })));
        }

        var collections = new Dictionary<string, List<object>>(StringComparer.Ordinal);
        var requested = query.Collection;
        if (requested is null || requested == "Statements") collections["Statements"] = statements;
        return collections;
    }

    private static void CollectSqlFiles(string dir, IReadOnlySet<string>? excluded, List<string> result)
    {
        try
        {
            foreach (var file in Directory.GetFiles(dir))
            {
                if (SqlExtensions.Contains(Path.GetExtension(file)))
                    result.Add(file);
            }
            foreach (var subDir in Directory.GetDirectories(dir))
            {
                var dirName = Path.GetFileName(subDir);
                if (excluded is not null && excluded.Contains(dirName)) continue;
                CollectSqlFiles(subDir, excluded, result);
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
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
