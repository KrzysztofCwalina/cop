// Carries Go type-category facts through the shared type model and source cache.

namespace Cop.Providers.SourceModel;

/// <summary>
/// A Go-specific <see cref="TypeDeclaration"/>. Go's two type forms (struct/interface) and
/// embedding don't map cleanly onto class-oriented checks, so <c>IsInterface</c>/<c>IsStruct</c>
/// make the distinction first-class. Only the Go provider emits these; the runtime maps this
/// CLR type to the cop type <c>GoType</c>. Round-trips through the source cache.
///
/// IsStruct distinguishes actual structs from other named types represented by the coarse
/// common kind; aliases are represented independently.
/// </summary>
public sealed record GoTypeDeclaration : TypeDeclaration
{
    /// <summary>Copies a shared type before Go-specific facts are initialized.</summary>
    public GoTypeDeclaration(TypeDeclaration source)
        : base(source)
    {
    }

    /// <summary>True for Go <c>interface</c> types.</summary>
    public bool IsInterface { get; init; }

    /// <summary>True for Go <c>struct</c> types.</summary>
    public bool IsStruct { get; init; }

    /// <summary>True for Go <c>type X = Y</c> aliases.</summary>
    public bool IsTypeAlias { get; init; }

    /// <summary>True for Go <c>struct</c> types with field tags.</summary>
    public bool HasStructTags { get; init; }

    /// <summary>True for Go <c>interface</c> type sets with union terms.</summary>
    public bool HasUnionTypeSet { get; init; }

    /// <summary>True for Go <c>interface</c> type sets with underlying type terms.</summary>
    public bool HasUnderlyingTypeTerms { get; init; }

    /// <inheritdoc />
    public override string? LanguageTag => "go";

    /// <inheritdoc />
    public override IReadOnlyList<KeyValuePair<string, bool>>? LanguageFlags =>
    [
        new("IsInterface", IsInterface),
        new("IsStruct", IsStruct),
        new("IsTypeAlias", IsTypeAlias),
        new("HasStructTags", HasStructTags),
        new("HasUnionTypeSet", HasUnionTypeSet),
        new("HasUnderlyingTypeTerms", HasUnderlyingTypeTerms),
    ];

    /// <summary>
    /// Registers reconstruction of Go type flags from cached declarations.
    /// </summary>
    public static void RegisterCacheFactory() =>
        LanguageTypeRegistry.Register("go", (baseDecl, flags) => new GoTypeDeclaration(baseDecl)
        {
            IsInterface = flags.TryGetValue("IsInterface", out bool i) && i,
            IsStruct = flags.TryGetValue("IsStruct", out bool s) && s,
            IsTypeAlias = flags.TryGetValue("IsTypeAlias", out bool a) && a,
            HasStructTags = flags.TryGetValue("HasStructTags", out bool t) && t,
            HasUnionTypeSet = flags.TryGetValue("HasUnionTypeSet", out bool u) && u,
            HasUnderlyingTypeTerms = flags.TryGetValue("HasUnderlyingTypeTerms", out bool h) && h
        });
}
