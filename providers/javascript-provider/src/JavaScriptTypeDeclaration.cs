// Preserves JavaScript export, inheritance, and generic facts in cached types.

namespace Cop.Providers.SourceModel;

/// <summary>
/// A JavaScript/TypeScript-specific <see cref="TypeDeclaration"/>. Only the JavaScript provider
/// emits these; the runtime maps this CLR type to the cop type <c>JavaScriptType</c> so
/// <c>:asJavaScript</c> checks can read them. Round-trips through the source cache.
///
/// Note: these facts are also derivable from the common model (<c>isPublic</c>, <c>BaseTypes</c>);
/// they exist to demonstrate the mechanism — prefer language-independent checks when they suffice.
/// </summary>
public sealed record JavaScriptTypeDeclaration : TypeDeclaration
{
    /// <summary>
    /// Copies a shared type before JavaScript-specific facts are initialized.
    /// </summary>
    public JavaScriptTypeDeclaration(TypeDeclaration source)
        : base(source)
    {
    }

    /// <summary>True for <c>export</c>ed classes.</summary>
    public bool IsExported { get; init; }

    /// <summary>True when the class <c>extends</c> a base class.</summary>
    public bool HasBaseClass { get; init; }

    /// <summary>True when the class is declared <c>abstract</c>.</summary>
    public new bool IsAbstract { get; init; }

    /// <summary>True when the class declares type parameters.</summary>
    public bool IsGeneric { get; init; }

    /// <summary>True when the class has an <c>implements</c> clause.</summary>
    public bool HasImplements { get; init; }

    /// <inheritdoc />
    public override string? LanguageTag => "javascript";

    /// <inheritdoc />
    public override IReadOnlyList<KeyValuePair<string, bool>>? LanguageFlags =>
    [
        new("IsExported", IsExported),
        new("HasBaseClass", HasBaseClass),
        new("IsAbstract", IsAbstract),
        new("IsGeneric", IsGeneric),
        new("HasImplements", HasImplements),
    ];

    /// <summary>
    /// Registers reconstruction of JavaScript type facts from source caches.
    /// </summary>
    public static void RegisterCacheFactory() =>
        LanguageTypeRegistry.Register("javascript", (baseDecl, flags) => new JavaScriptTypeDeclaration(baseDecl)
        {
            IsExported = flags.TryGetValue("IsExported", out bool x) && x,
            HasBaseClass = flags.TryGetValue("HasBaseClass", out bool b) && b,
            IsAbstract = flags.TryGetValue("IsAbstract", out bool a) && a,
            IsGeneric = flags.TryGetValue("IsGeneric", out bool g) && g,
            HasImplements = flags.TryGetValue("HasImplements", out bool i) && i
        });
}
