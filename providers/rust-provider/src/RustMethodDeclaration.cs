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

    public override string? LanguageTag => "rust";

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
            isUnsafe: flags.TryGetValue("IsUnsafe", out var u) && u,
            isConst: flags.TryGetValue("IsConst", out var c) && c,
            isExtern: flags.TryGetValue("IsExtern", out var e) && e));
}

public static class RustMethodDeclarationExtensions
{
    /// <summary>Wraps a common <see cref="MethodDeclaration"/> as a Rust-specific one.</summary>
    public static RustMethodDeclaration AsRust(
        this MethodDeclaration source,
        bool isUnsafe = false,
        bool isConst = false,
        bool isExtern = false)
        => new(source, isUnsafe, isConst, isExtern);
}
