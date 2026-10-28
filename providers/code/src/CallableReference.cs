namespace Cop.Providers.SourceModel;

/// <summary>A compiler-selected callable declaration, not a runtime dispatch target.</summary>
public record CallableReference(string Name, string Signature)
{
    public string? DeclaringType { get; init; }
    public string? Namespace { get; init; }
    public TypeReference? ReturnType { get; init; }
    public List<TypeReference> Parameters { get; init; } = [];
    public string? SourceFile { get; init; }
    public int? Line { get; init; }
    public int? Column { get; init; }
    public int? EndLine { get; init; }
    public int? EndColumn { get; init; }
}
