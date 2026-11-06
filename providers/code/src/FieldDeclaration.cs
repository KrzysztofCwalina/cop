// Represents field declarations, types, and modifiers.

namespace Cop.Providers.SourceModel;

/// <summary>
/// A parsed field declaration with modifiers and a declared type.
/// </summary>
public record FieldDeclaration(string Name, TypeReference? Type, Modifier Modifiers, int Line)
{
    /// <summary>
    /// True when the declaration has public accessibility.
    /// </summary>
    public bool IsPublic => Modifiers.HasFlag(Modifier.Public);
    /// <summary>
    /// True when the declaration has private accessibility.
    /// </summary>
    public bool IsPrivate => Modifiers.HasFlag(Modifier.Private);
    /// <summary>
    /// True when the declaration has protected accessibility.
    /// </summary>
    public bool IsProtected => Modifiers.HasFlag(Modifier.Protected);
    /// <summary>
    /// True when the declaration has internal accessibility.
    /// </summary>
    public bool IsInternal => Modifiers.HasFlag(Modifier.Internal);
    /// <summary>
    /// True when the declaration is static.
    /// </summary>
    public bool IsStatic => Modifiers.HasFlag(Modifier.Static);
    /// <summary>
    /// True when the field has the readonly modifier.
    /// </summary>
    public bool IsReadonly => Modifiers.HasFlag(Modifier.Readonly);
    /// <summary>
    /// True when the field declares a compile-time constant.
    /// </summary>
    public bool IsConst => Modifiers.HasFlag(Modifier.Const);
}
