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
    public JavaScriptTypeDeclaration(TypeDeclaration source, bool isExported, bool hasBaseClass)
        : base(source)
    {
        IsExported = isExported;
        HasBaseClass = hasBaseClass;
    }

    /// <summary>True for <c>export</c>ed classes.</summary>
    public bool IsExported { get; init; }

    /// <summary>True when the class <c>extends</c> a base class.</summary>
    public bool HasBaseClass { get; init; }

    public override string? LanguageTag => "javascript";

    public override IReadOnlyList<KeyValuePair<string, bool>>? LanguageFlags =>
    [
        new("IsExported", IsExported),
        new("HasBaseClass", HasBaseClass),
    ];

    public static void RegisterCacheFactory() =>
        LanguageTypeRegistry.Register("javascript", (baseDecl, flags) => new JavaScriptTypeDeclaration(
            baseDecl,
            isExported: flags.TryGetValue("IsExported", out var x) && x,
            hasBaseClass: flags.TryGetValue("HasBaseClass", out var b) && b));
}

public static class JavaScriptTypeDeclarationExtensions
{
    public static JavaScriptTypeDeclaration AsJavaScript(this TypeDeclaration source, bool isExported = false, bool hasBaseClass = false)
        => new(source, isExported, hasBaseClass);
}
