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
    /// Copies a shared type and adds JavaScript declaration-specific facts.
    /// </summary>
    public JavaScriptTypeDeclaration(TypeDeclaration source, bool isExported, bool hasBaseClass,
        bool isAbstract = false, bool isGeneric = false, bool hasImplements = false)
        : base(source)
    {
        IsExported = isExported;
        HasBaseClass = hasBaseClass;
        IsAbstract = isAbstract;
        IsGeneric = isGeneric;
        HasImplements = hasImplements;
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
        LanguageTypeRegistry.Register("javascript", (baseDecl, flags) => new JavaScriptTypeDeclaration(
            baseDecl,
            isExported: flags.TryGetValue("IsExported", out bool x) && x,
            hasBaseClass: flags.TryGetValue("HasBaseClass", out bool b) && b,
            isAbstract: flags.TryGetValue("IsAbstract", out bool a) && a,
            isGeneric: flags.TryGetValue("IsGeneric", out bool g) && g,
            hasImplements: flags.TryGetValue("HasImplements", out bool i) && i));
}

/// <summary>Converts shared types to JavaScript-specific declarations.</summary>
public static class JavaScriptTypeDeclarationExtensions
{
    /// <summary>Wraps a shared type with JavaScript declaration-specific facts.</summary>
    public static JavaScriptTypeDeclaration AsJavaScript(this TypeDeclaration source, bool isExported = false,
        bool hasBaseClass = false, bool isAbstract = false, bool isGeneric = false, bool hasImplements = false)
        => new(source, isExported, hasBaseClass, isAbstract, isGeneric, hasImplements);
}
