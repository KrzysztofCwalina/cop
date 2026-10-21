namespace Cop.Providers.SourceModel;

/// <summary>
/// Parsed expression facts, not source text. Unsupported syntax has kind "other";
/// string literals contain their decoded value, without quotes or escape syntax.
/// </summary>
public sealed record ExpressionInfo(string Kind)
{
    public string? Name { get; init; }
    public string? StringValue { get; init; }
    public ExpressionInfo? Target { get; init; }
    public List<ExpressionInfo> Children { get; init; } = [];
}
