namespace Cop.Providers.SourceModel;

public record MethodDeclaration(
    string Name,
    Modifier Modifiers,
    List<string> Decorators,
    TypeReference? ReturnType,
    List<ParameterDeclaration> Parameters,
    int Line)
{
    public bool IsPublic => Modifiers.HasFlag(Modifier.Public);
    public bool IsProtected => Modifiers.HasFlag(Modifier.Protected);
    public bool IsAsync => Modifiers.HasFlag(Modifier.Async);
    public bool IsStatic => Modifiers.HasFlag(Modifier.Static);
    public bool IsAbstract => Modifiers.HasFlag(Modifier.Abstract);
    public bool IsVirtual => Modifiers.HasFlag(Modifier.Virtual);
    public bool IsOverride => Modifiers.HasFlag(Modifier.Override);
    public bool IsPrivate => Modifiers.HasFlag(Modifier.Private);
    public bool IsInternal => Modifiers.HasFlag(Modifier.Internal);
    public List<StatementInfo> Statements { get; init; } = [];
}
