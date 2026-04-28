namespace Cop.Providers.SourceModel;

public record FieldDeclaration(string Name, TypeReference? Type, Modifier Modifiers, int Line)
{
    public bool IsPublic => Modifiers.HasFlag(Modifier.Public);
    public bool IsPrivate => Modifiers.HasFlag(Modifier.Private);
    public bool IsProtected => Modifiers.HasFlag(Modifier.Protected);
    public bool IsInternal => Modifiers.HasFlag(Modifier.Internal);
    public bool IsStatic => Modifiers.HasFlag(Modifier.Static);
    public bool IsReadonly => Modifiers.HasFlag(Modifier.Readonly);
    public bool IsConst => Modifiers.HasFlag(Modifier.Const);
}
