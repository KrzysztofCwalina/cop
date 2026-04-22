namespace Cop.Providers.SourceModel;

public record TypeDeclaration(
    string Name,
    TypeKind Kind,
    Modifier Modifiers,
    List<string> BaseTypes,
    List<string> Decorators,
    List<MethodDeclaration> Constructors,
    List<MethodDeclaration> Methods,
    List<TypeDeclaration> NestedTypes,
    List<string> EnumValues,
    int Line)
{
    public bool IsPublic => Modifiers.HasFlag(Modifier.Public);
    public bool IsSealed => Modifiers.HasFlag(Modifier.Sealed);
    public bool IsAbstract => Modifiers.HasFlag(Modifier.Abstract);
    public bool IsStatic => Modifiers.HasFlag(Modifier.Static);

    public SourceFile? File { get; init; }
    public bool HasDocComment { get; init; }
    public List<FieldDeclaration> Fields { get; init; } = [];
    public List<PropertyDeclaration> Properties { get; init; } = [];
    public List<EventDeclaration> Events { get; init; } = [];
    public string Source => Name;

    public bool InheritsFrom(string name) =>
        BaseTypes.Any(b => b == name || b.EndsWith("." + name));
}
