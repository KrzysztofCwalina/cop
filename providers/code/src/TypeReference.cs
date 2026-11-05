// Represents referenced types, namespaces, and generic arguments.

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
    /// <summary>Whether the compiler identifies this as an anonymous type.</summary>
    public bool IsAnonymous { get; init; }

    public bool IsGeneric => GenericArguments.Count > 0;
    public override string ToString() => OriginalText;
}
