// Represents type declarations, members, inheritance, and source identity.

namespace Cop.Providers.SourceModel;

/// <summary>
/// A parsed type with member declarations, modifiers, and inheritance facts.
/// </summary>
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
    /// <summary>
    /// The namespace that contains the declaration.
    /// </summary>
    public string? Namespace { get; init; }
    /// <summary>
    /// The parsed type targeted by this type alias, when present.
    /// </summary>
    public TypeReference? AliasTarget { get; init; }
    /// <summary>
    /// The generic type-parameter names in declaration order.
    /// </summary>
    public List<string> TypeParameters { get; init; } = [];
    /// <summary>
    /// The one-based source column, or null when the parser cannot provide it.
    /// </summary>
    public int? Column { get; init; }
    /// <summary>
    /// The one-based ending source line, when available.
    /// </summary>
    public int? EndLine { get; init; }
    /// <summary>
    /// The one-based exclusive ending source column, when available.
    /// </summary>
    public int? EndColumn { get; init; }
    /// <summary>
    /// True when the declaration has public accessibility.
    /// </summary>
    public bool IsPublic => Modifiers.HasFlag(Modifier.Public);
    /// <summary>
    /// True when the type prevents further inheritance.
    /// </summary>
    public bool IsSealed => Modifiers.HasFlag(Modifier.Sealed);
    /// <summary>
    /// True when the declaration is abstract.
    /// </summary>
    public bool IsAbstract => Modifiers.HasFlag(Modifier.Abstract);
    /// <summary>
    /// True when the declaration is static.
    /// </summary>
    public bool IsStatic => Modifiers.HasFlag(Modifier.Static);

    /// <summary>
    /// The source file that owns this declaration or source location.
    /// </summary>
    public SourceFile? File { get; set; }
    private List<MethodDeclaration> _declaredMethods = Methods;
    private List<MethodDeclaration>? _resolvedMethods;
    private Lazy<List<MethodDeclaration>>? _deferredMethods;
    private AsyncProviderValue<List<MethodDeclaration>>? _asyncMethods;
    private AsyncProviderValue<bool>? _asyncSemantics;
    /// <summary>
    /// The methods declared by this type.
    /// </summary>
    public List<MethodDeclaration> Methods
    {
        get => _resolvedMethods ?? _deferredMethods?.Value ?? _declaredMethods;
        init => _declaredMethods = value;
    }
    internal List<MethodDeclaration> DeclaredMethods => _declaredMethods;
    internal void DeferMembers(Func<List<MethodDeclaration>> resolve) => _deferredMethods = new(resolve);
    internal void DeferMembersAsync(
        Func<CancellationToken, ValueTask<List<MethodDeclaration>>> resolve) =>
        _asyncMethods = new(resolve);

    /// <summary>Resolves cross-file members only when a query demands this type's methods.</summary>
    public async ValueTask EnsureMembersAsync(CancellationToken cancellationToken = default)
    {
        if (_asyncMethods is not null)
        {
            _resolvedMethods = await _asyncMethods.GetValueAsync(cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>Defers process-backed interface and type binding.</summary>
    public void DeferSemanticAnalysisAsync(AsyncProviderValue<bool> analysis) =>
        _asyncSemantics = analysis;

    /// <summary>Awaits this type's process-backed semantic analysis.</summary>
    public async ValueTask EnsureSemanticAnalysisAsync(CancellationToken cancellationToken = default)
    {
        if (_asyncSemantics is not null)
        {
            await _asyncSemantics.GetValueAsync(cancellationToken).ConfigureAwait(false);
        }
    }
    /// <summary>
    /// True when a documentation comment is attached to the declaration.
    /// </summary>
    public bool HasDocComment { get; init; }
    /// <summary>
    /// The documentation comment attached to the declaration.
    /// </summary>
    public string? DocComment { get; init; }
    /// <summary>
    /// The fields declared by this type.
    /// </summary>
    public List<FieldDeclaration> Fields { get; init; } = [];
    /// <summary>
    /// The properties declared by this type.
    /// </summary>
    public List<PropertyDeclaration> Properties { get; init; } = [];
    /// <summary>
    /// The events declared by this type.
    /// </summary>
    public List<EventDeclaration> Events { get; init; } = [];

    /// <summary>
    /// All interfaces this type implements (including inherited), resolved via semantic analysis.
    /// Empty if semantic analysis is unavailable.
    /// </summary>
    internal Lazy<bool>? DeferredSemantics { get; set; }
    private List<string> _interfaces = [];
    internal List<string> CachedInterfaces => _interfaces;
    /// <summary>
    /// The parsed base-type or implemented-interface names.
    /// </summary>
    public List<string> Interfaces
    {
        get { _ = DeferredSemantics?.Value; return _interfaces; }
        set => _interfaces = value;
    }

    /// <summary>
    /// The display source location used in diagnostics.
    /// </summary>
    public string Source => Name;

    /// <summary>
    /// Tests whether this type names the supplied base type.
    /// </summary>
    public bool InheritsFrom(string name) =>
        BaseTypes.Any(b => b == name || b.EndsWith("." + name));

    /// <summary>
    /// Tests whether this type names the supplied interface.
    /// </summary>
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
