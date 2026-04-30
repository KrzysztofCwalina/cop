namespace Cop.Lang;

public record CommandBlock(
    string Name,
    string MessageTemplate,
    string? Collection,
    List<Expression> Filters,
    int Line,
    string? DocComment = null,
    bool IsCommand = false,
    bool IsExported = false,
    string? ActionName = null,
    string? OutputPath = null,
    Expression? Guard = null,
    string? CommandRef = null,
    Expression? Exclusions = null,
    List<string>? Parameters = null,
    Expression? OutputExpression = null,
    SinkTarget? Sink = null,
    string? PathOverride = null);

/// <summary>
/// Represents the target sink in a chained pipeline: foreach Source => Transform => Sink
/// </summary>
public record SinkTarget(string Name, List<object>? Args = null)
{
    /// <summary>
    /// The qualified sink name (e.g., "http.Send", "console.WriteLine", "file.Write").
    /// </summary>
    public string Name { get; init; } = Name;

    /// <summary>
    /// Optional arguments (e.g., file path for file.Write('path')).
    /// </summary>
    public List<object>? Args { get; init; } = Args;
}

