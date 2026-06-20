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
    public string? DocComment { get; init; }
    public List<FieldDeclaration> Fields { get; init; } = [];
    public List<PropertyDeclaration> Properties { get; init; } = [];
    public List<EventDeclaration> Events { get; init; } = [];

    /// <summary>
    /// All interfaces this type implements (including inherited), resolved via semantic analysis.
    /// Empty if semantic analysis is unavailable.
    /// </summary>
    public List<string> Interfaces { get; set; } = [];

    public string Source => Name;

    public bool InheritsFrom(string name) =>
        BaseTypes.Any(b => b == name || b.EndsWith("." + name));

    public bool Implements(string interfaceName) =>
        Interfaces.Any(i => i == interfaceName || i.EndsWith("." + interfaceName));

    /// <summary>
    /// Language-specific subtype tag for disk-cache round-tripping (e.g. "rust").
    /// Null for the language-agnostic base type. Subclasses that add language-specific
    /// fields override this together with <see cref="LanguageFlags"/> and register a
    /// reconstruction factory via <see cref="LanguageTypeRegistry"/>.
    /// </summary>
    public virtual string? LanguageTag => null;

    /// <summary>
    /// Language-specific boolean facts serialized alongside the base type in the cache.
    /// Null for the base type.
    /// </summary>
    public virtual IReadOnlyList<KeyValuePair<string, bool>>? LanguageFlags => null;
}
