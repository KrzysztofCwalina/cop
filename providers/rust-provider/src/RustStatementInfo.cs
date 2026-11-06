// Preserves Rust-specific statement identities through the shared source cache.

namespace Cop.Providers.SourceModel;

/// <summary>
/// A Rust-specific <see cref="StatementInfo"/> exposing Rust-only statement facts (macro calls,
/// panic-like macros) that the language-agnostic model can't represent. Only the Rust provider
/// emits these; the runtime maps this CLR type to the cop type <c>RustStatement</c>. The flags
/// derive from the base statement's <see cref="StatementInfo.Kind"/> / <see cref="StatementInfo.MemberName"/>
/// (both serialized), so cache round-tripping only needs to re-wrap the base.
/// </summary>
public sealed class RustStatementInfo : StatementInfo
{
    /// <summary>Copies neutral statement facts and their shared graph references.</summary>
    public RustStatementInfo(StatementInfo source)
        : base(source)
    {
    }

    /// <summary>True for a Rust macro invocation (e.g. <c>println!</c>, <c>vec!</c>).</summary>
    public bool IsMacroCall => MemberName != null && MemberName.EndsWith("!", System.StringComparison.Ordinal);

    /// <summary>True for a panic-like macro (<c>panic!</c> / <c>todo!</c> / <c>unimplemented!</c> / <c>unreachable!</c>).</summary>
    public bool IsPanic => Kind == "throw";

    /// <inheritdoc />
    public override string? LanguageTag => "rust";

    /// <inheritdoc />
    public override IReadOnlyList<KeyValuePair<string, bool>>? LanguageFlags => [];

    /// <summary>Registers the cache reconstruction factory so cached Rust statements load back as
    /// <see cref="RustStatementInfo"/> on cache hits. Idempotent.</summary>
    public static void RegisterCacheFactory() =>
        StatementTypeRegistry.Register("rust", (baseDecl, flags) => new RustStatementInfo(baseDecl));
}
