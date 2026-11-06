// Preserves Rust type-category facts through shared types and source caching.

namespace Cop.Providers.SourceModel;

/// <summary>
/// A Rust-specific <see cref="TypeDeclaration"/> carrying Rust-only facts that have no place
/// in the language-agnostic common model (traits, synthetic impl blocks, unsafe, and Rust
/// declaration shapes).
///
/// Only the Rust provider emits these. The runtime maps this CLR type to the cop type
/// <c>RustType</c> (declared in the rust package), so checks that narrow with <c>:asRust</c>
/// can read these fields while the common <c>Type</c> stays clean. Because the Rust provider
/// caches parsed sources to disk, this type also round-trips through the cache via
/// <see cref="LanguageTag"/>/<see cref="LanguageFlags"/> + <see cref="RegisterCacheFactory"/>.
/// </summary>
public sealed record RustTypeDeclaration : TypeDeclaration
{
    /// <summary>
    /// Copies a shared type before Rust-specific facts are initialized.
    /// </summary>
    public RustTypeDeclaration(TypeDeclaration source)
        : base(source)
    {
    }

    /// <summary>True for Rust <c>trait</c> declarations.</summary>
    public bool IsTrait { get; init; }

    /// <summary>True for synthetic types representing an <c>impl</c> block.</summary>
    public bool IsImpl { get; init; }

    /// <summary>True for <c>unsafe</c> traits and impls.</summary>
    public bool IsUnsafe { get; init; }

    /// <summary>True for Rust <c>union</c> declarations.</summary>
    public bool IsUnion { get; init; }

    /// <summary>True for tuple structs, e.g. <c>struct S(T);</c>.</summary>
    public bool IsTupleStruct { get; init; }

    /// <summary>True for unit structs, e.g. <c>struct S;</c>.</summary>
    public bool IsUnitStruct { get; init; }

    /// <summary>True for negative impls, e.g. <c>impl !Send for T</c>.</summary>
    public bool IsNegativeImpl { get; init; }

    /// <inheritdoc />
    public override string? LanguageTag => "rust";

    /// <inheritdoc />
    public override IReadOnlyList<KeyValuePair<string, bool>>? LanguageFlags =>
    [
        new("IsTrait", IsTrait),
        new("IsImpl", IsImpl),
        new("IsUnsafe", IsUnsafe),
        new("IsUnion", IsUnion),
        new("IsTupleStruct", IsTupleStruct),
        new("IsUnitStruct", IsUnitStruct),
        new("IsNegativeImpl", IsNegativeImpl),
    ];

    /// <summary>
    /// Registers the cache reconstruction factory so cached Rust types load back as
    /// <see cref="RustTypeDeclaration"/> (not the plain base) on cache hits. Idempotent.
    /// </summary>
    public static void RegisterCacheFactory() =>
        LanguageTypeRegistry.Register("rust", (baseDecl, flags) => new RustTypeDeclaration(baseDecl)
        {
            IsTrait = flags.TryGetValue("IsTrait", out bool t) && t,
            IsImpl = flags.TryGetValue("IsImpl", out bool i) && i,
            IsUnsafe = flags.TryGetValue("IsUnsafe", out bool u) && u,
            IsUnion = flags.TryGetValue("IsUnion", out bool union) && union,
            IsTupleStruct = flags.TryGetValue("IsTupleStruct", out bool tupleStruct) && tupleStruct,
            IsUnitStruct = flags.TryGetValue("IsUnitStruct", out bool unitStruct) && unitStruct,
            IsNegativeImpl = flags.TryGetValue("IsNegativeImpl", out bool negativeImpl) && negativeImpl
        });
}
