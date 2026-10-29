namespace Cop.Providers.SourceModel;

/// <summary>Parsed import syntax facts, not resolved symbol bindings.</summary>
public record ImportInfo(string Module, int Line)
{
    public string? Name { get; init; }
    public string? Alias { get; init; }
    public bool IsWildcard { get; init; }
    public bool IsStatic { get; init; }
    public int RelativeLevel { get; init; }
    public int? Column { get; init; }
    public int? EndLine { get; init; }
    public int? EndColumn { get; init; }
}
