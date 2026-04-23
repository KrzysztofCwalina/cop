namespace Cop.Providers.SourceModel;

/// <summary>
/// Structured type reference with name, namespace, and generic arguments.
/// ToString() preserves original syntax text for backward compatibility.
/// </summary>
public record TypeReference(
    string Name,
    string? Namespace,
    List<TypeReference> GenericArguments,
    string OriginalText)
{
    public bool IsGeneric => GenericArguments.Count > 0;
    public override string ToString() => OriginalText;
}
