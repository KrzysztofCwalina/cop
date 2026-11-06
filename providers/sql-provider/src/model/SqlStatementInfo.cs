// Models a SQL statement category and its source location.

namespace Cop.Providers.Sql;

/// <summary>
/// A SQL statement discovered in a .sql file.
/// </summary>
public record SqlStatementInfo(string Kind, string Text, int Line, bool SelectsStar, bool HasWhere)
{
    /// <summary>Gets the SQL source file containing this statement.</summary>
    public SourceModel.SourceFile? File { get; init; }
    /// <summary>Gets the file path and line used to locate diagnostics.</summary>
    public string Source => $"{File?.Path}:{Line}";
}
