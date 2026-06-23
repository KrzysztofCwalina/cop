namespace Cop.Providers.Sql;

/// <summary>
/// A SQL statement discovered in a .sql file.
/// </summary>
public record SqlStatementInfo(string Kind, string Text, int Line, bool SelectsStar, bool HasWhere)
{
    public SourceModel.SourceFile? File { get; init; }
    public string Source => $"{File?.Path}:{Line}";
}
