namespace Cop.Providers.SourceModel;

/// <summary>
/// A Rust-specific <see cref="TypeDeclaration"/> carrying Rust-only facts that have no place
/// in the language-agnostic common model (traits, synthetic impl blocks, unsafe).
///
/// Only the Rust provider emits these. The runtime maps this CLR type to the cop type
/// <c>RustType</c> (declared in the rust package), so checks that narrow with <c>:asRust</c>
/// can read these fields while the common <c>Type</c> stays clean. Because the Rust provider
/// caches parsed sources to disk, this type also round-trips through the cache via
/// <see cref="LanguageTag"/>/<see cref="LanguageFlags"/> + <see cref="RegisterCacheFactory"/>.
/// </summary>
public sealed record RustTypeDeclaration : TypeDeclaration
{
    public RustTypeDeclaration(TypeDeclaration source, bool isTrait, bool isImpl, bool isUnsafe)
        : base(source)
    {
        IsTrait = isTrait;
        IsImpl = isImpl;
        IsUnsafe = isUnsafe;
    }

    /// <summary>True for Rust <c>trait</c> declarations.</summary>
    public bool IsTrait { get; init; }

    /// <summary>True for synthetic types representing an <c>impl</c> block.</summary>
    public bool IsImpl { get; init; }

    /// <summary>True for <c>unsafe</c> traits and impls.</summary>
    public bool IsUnsafe { get; init; }

    public override string? LanguageTag => "rust";

    public override IReadOnlyList<KeyValuePair<string, bool>>? LanguageFlags =>
    [
        new("IsTrait", IsTrait),
        new("IsImpl", IsImpl),
        new("IsUnsafe", IsUnsafe),
    ];

    /// <summary>
    /// Registers the cache reconstruction factory so cached Rust types load back as
    /// <see cref="RustTypeDeclaration"/> (not the plain base) on cache hits. Idempotent.
    /// </summary>
    public static void RegisterCacheFactory() =>
        LanguageTypeRegistry.Register("rust", (baseDecl, flags) => new RustTypeDeclaration(
            baseDecl,
            isTrait: flags.TryGetValue("IsTrait", out var t) && t,
            isImpl: flags.TryGetValue("IsImpl", out var i) && i,
            isUnsafe: flags.TryGetValue("IsUnsafe", out var u) && u));
}

public static class RustTypeDeclarationExtensions
{
    /// <summary>Wraps a common <see cref="TypeDeclaration"/> as a Rust-specific one.</summary>
    public static RustTypeDeclaration AsRust(this TypeDeclaration source, bool isTrait = false, bool isImpl = false, bool isUnsafe = false)
        => new(source, isTrait, isImpl, isUnsafe);
}
