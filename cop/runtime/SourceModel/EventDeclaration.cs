namespace Cop.Providers.SourceModel;

public record EventDeclaration(string Name, TypeReference? Type, Modifier Modifiers, int Line)
{
    public bool IsPublic => Modifiers.HasFlag(Modifier.Public);
    public bool IsProtected => Modifiers.HasFlag(Modifier.Protected);
    public bool IsPrivate => Modifiers.HasFlag(Modifier.Private);
    public bool IsInternal => Modifiers.HasFlag(Modifier.Internal);
    public bool IsStatic => Modifiers.HasFlag(Modifier.Static);
}
