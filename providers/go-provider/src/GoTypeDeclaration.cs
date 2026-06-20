namespace Cop.Providers.SourceModel;

/// <summary>
/// A Go-specific <see cref="TypeDeclaration"/>. Go's two type forms (struct/interface) and
/// embedding don't map cleanly onto class-oriented checks, so <c>IsInterface</c>/<c>IsStruct</c>
/// make the distinction first-class. Only the Go provider emits these; the runtime maps this
/// CLR type to the cop type <c>GoType</c>. Round-trips through the source cache.
///
/// Note: these particular facts are also derivable from the common <c>Kind</c>; they exist to
/// demonstrate the mechanism — prefer language-independent checks when they suffice.
/// </summary>
public sealed record GoTypeDeclaration : TypeDeclaration
{
    public GoTypeDeclaration(TypeDeclaration source, bool isInterface, bool isStruct)
        : base(source)
    {
        IsInterface = isInterface;
        IsStruct = isStruct;
    }

    /// <summary>True for Go <c>interface</c> types.</summary>
    public bool IsInterface { get; init; }

    /// <summary>True for Go <c>struct</c> types.</summary>
    public bool IsStruct { get; init; }

    public override string? LanguageTag => "go";

    public override IReadOnlyList<KeyValuePair<string, bool>>? LanguageFlags =>
    [
        new("IsInterface", IsInterface),
        new("IsStruct", IsStruct),
    ];

    public static void RegisterCacheFactory() =>
        LanguageTypeRegistry.Register("go", (baseDecl, flags) => new GoTypeDeclaration(
            baseDecl,
            isInterface: flags.TryGetValue("IsInterface", out var i) && i,
            isStruct: flags.TryGetValue("IsStruct", out var s) && s));
}

public static class GoTypeDeclarationExtensions
{
    public static GoTypeDeclaration AsGo(this TypeDeclaration source, bool isInterface = false, bool isStruct = false)
        => new(source, isInterface, isStruct);
}
