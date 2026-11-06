// Represents event declarations and their visibility.

namespace Cop.Providers.SourceModel;

/// <summary>
/// A parsed event declaration with accessibility and source facts.
/// </summary>
public record EventDeclaration(string Name, TypeReference? Type, Modifier Modifiers, int Line)
{
    /// <summary>
    /// True when the declaration has public accessibility.
    /// </summary>
    public bool IsPublic => Modifiers.HasFlag(Modifier.Public);
    /// <summary>
    /// True when the declaration has protected accessibility.
    /// </summary>
    public bool IsProtected => Modifiers.HasFlag(Modifier.Protected);
    /// <summary>
    /// True when the declaration has private accessibility.
    /// </summary>
    public bool IsPrivate => Modifiers.HasFlag(Modifier.Private);
    /// <summary>
    /// True when the declaration has internal accessibility.
    /// </summary>
    public bool IsInternal => Modifiers.HasFlag(Modifier.Internal);
    /// <summary>
    /// True when the declaration is static.
    /// </summary>
    public bool IsStatic => Modifiers.HasFlag(Modifier.Static);
}
