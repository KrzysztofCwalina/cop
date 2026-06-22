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
    public List<StatementInfo> Statements { get; set; } = [];
    public bool HasDocComment { get; init; }
    public string? DocComment { get; init; }

    /// <summary>The source file declaring this method. Set during reference linking.</summary>
    public SourceFile? File { get; set; }

    /// <summary>Stable identity string for this method (file path + name).</summary>
    public string Source => File is null ? Name : $"{File.Path}:{Name}";

    /// <summary>
    /// Language-specific subtype tag for disk-cache round-tripping (e.g. "csharp"). Null for
    /// the language-agnostic base method. Subclasses that add language-specific fields override
    /// this together with <see cref="LanguageFlags"/> and register a reconstruction factory via
    /// <see cref="MethodTypeRegistry"/>.
    /// </summary>
    public virtual string? LanguageTag => null;

    /// <summary>Language-specific boolean facts serialized alongside the base method. Null for the base.</summary>
    public virtual IReadOnlyList<KeyValuePair<string, bool>>? LanguageFlags => null;
}
