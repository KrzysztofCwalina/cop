namespace Cop.Providers.SourceModel;

public record PropertyDeclaration(string Name, TypeReference? Type, Modifier Modifiers, int Line)
{
    public bool IsPublic => Modifiers.HasFlag(Modifier.Public);
    public bool IsProtected => Modifiers.HasFlag(Modifier.Protected);
    public bool IsPrivate => Modifiers.HasFlag(Modifier.Private);
    public bool IsInternal => Modifiers.HasFlag(Modifier.Internal);
    public bool IsStatic => Modifiers.HasFlag(Modifier.Static);
    public bool IsAbstract => Modifiers.HasFlag(Modifier.Abstract);
    public bool IsVirtual => Modifiers.HasFlag(Modifier.Virtual);
    public bool IsOverride => Modifiers.HasFlag(Modifier.Override);
    public bool HasGetter { get; init; }
    public bool HasSetter { get; init; }
    public bool HasDocComment { get; init; }
}
