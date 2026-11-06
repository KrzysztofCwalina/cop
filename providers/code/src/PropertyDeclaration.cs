// Represents property declarations and accessor availability.

namespace Cop.Providers.SourceModel;

/// <summary>
/// A parsed property with accessor, modifier, and documentation facts.
/// </summary>
public record PropertyDeclaration(string Name, TypeReference? Type, Modifier Modifiers, int Line)
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
    /// <summary>
    /// True when the declaration is abstract.
    /// </summary>
    public bool IsAbstract => Modifiers.HasFlag(Modifier.Abstract);
    /// <summary>
    /// True when the declaration has the virtual modifier.
    /// </summary>
    public bool IsVirtual => Modifiers.HasFlag(Modifier.Virtual);
    /// <summary>
    /// True when the declaration overrides an inherited member.
    /// </summary>
    public bool IsOverride => Modifiers.HasFlag(Modifier.Override);
    /// <summary>
    /// True when the property declares a getter.
    /// </summary>
    public bool HasGetter { get; init; }
    /// <summary>
    /// True when the property declares a setter.
    /// </summary>
    public bool HasSetter { get; init; }
    /// <summary>
    /// True when a documentation comment is attached to the declaration.
    /// </summary>
    public bool HasDocComment { get; init; }
    /// <summary>
    /// The documentation comment attached to the declaration.
    /// </summary>
    public string? DocComment { get; init; }
}
