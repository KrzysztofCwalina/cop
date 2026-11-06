// Preserves Rust unsafe, const, and extern function facts through source caching.

namespace Cop.Providers.SourceModel;

/// <summary>
/// A Rust-specific <see cref="MethodDeclaration"/> carrying Rust-only function facts that have
/// no place in the language-agnostic common model (<c>unsafe</c> / <c>const</c> / <c>extern</c>
/// functions). Only the Rust provider emits these; the runtime maps this CLR type to the cop
/// type <c>RustMethod</c>. Because the Rust provider caches parsed sources to disk, this type
/// round-trips via <see cref="LanguageTag"/>/<see cref="LanguageFlags"/> + <see cref="RegisterCacheFactory"/>.
/// </summary>
public sealed record RustMethodDeclaration : MethodDeclaration
{
    /// <summary>Copies a shared method and adds Rust function qualifier facts.</summary>
    public RustMethodDeclaration(
        MethodDeclaration source,
        bool isUnsafe = false,
        bool isConst = false,
        bool isExtern = false)
        : base(source)
    {
        IsUnsafe = isUnsafe;
        IsConst = isConst;
        IsExtern = isExtern;
    }

    /// <summary>True for <c>unsafe fn</c> declarations.</summary>
    public bool IsUnsafe { get; init; }

    /// <summary>True for <c>const fn</c> declarations.</summary>
    public bool IsConst { get; init; }

    /// <summary>True for <c>extern</c> functions (FFI, e.g. <c>extern "C" fn</c>).</summary>
    public bool IsExtern { get; init; }

    /// <inheritdoc />
    public override string? LanguageTag => "rust";

    /// <inheritdoc />
    public override IReadOnlyList<KeyValuePair<string, bool>>? LanguageFlags =>
    [
        new("IsUnsafe", IsUnsafe),
        new("IsConst", IsConst),
        new("IsExtern", IsExtern),
    ];

    /// <summary>Registers the cache reconstruction factory so cached Rust methods load back as
    /// <see cref="RustMethodDeclaration"/> on cache hits. Idempotent.</summary>
    public static void RegisterCacheFactory() =>
        MethodTypeRegistry.Register("rust", (baseDecl, flags) => new RustMethodDeclaration(
            baseDecl,
            isUnsafe: flags.TryGetValue("IsUnsafe", out bool u) && u,
            isConst: flags.TryGetValue("IsConst", out bool c) && c,
            isExtern: flags.TryGetValue("IsExtern", out bool e) && e));
}
