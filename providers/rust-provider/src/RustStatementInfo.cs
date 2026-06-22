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
    /// <summary>Parser constructor — matches the base 7-arg constructor.</summary>
    public RustStatementInfo(
        string kind, List<string> keywords, string? typeName, string? memberName,
        List<string> arguments, int line, bool isInMethod)
        : base(kind, keywords, typeName, memberName, arguments, line, isInMethod) { }

    /// <summary>Cache reconstruction constructor — copies fields from a deserialized base.</summary>
    public RustStatementInfo(StatementInfo source)
        : base(source.Kind, source.Keywords, source.TypeName, source.MemberName,
               source.Arguments, source.Line, source.IsInMethod)
    {
        HasRethrow = source.HasRethrow;
        IsErrorHandler = source.IsErrorHandler;
        IsGenericErrorHandler = source.IsGenericErrorHandler;
        IsBraced = source.IsBraced;
        Method = source.Method;
        Condition = source.Condition;
        Expression = source.Expression;
        File = source.File;
        Parent = source.Parent;
        CopIgnore = source.CopIgnore;
        ConstructedTypeInterfaces = source.ConstructedTypeInterfaces;
        _children = source._children;
    }

    /// <summary>True for a Rust macro invocation (e.g. <c>println!</c>, <c>vec!</c>).</summary>
    public bool IsMacroCall => MemberName != null && MemberName.EndsWith("!", System.StringComparison.Ordinal);

    /// <summary>True for a panic-like macro (<c>panic!</c> / <c>todo!</c> / <c>unimplemented!</c> / <c>unreachable!</c>).</summary>
    public bool IsPanic => Kind == "throw";

    public override string? LanguageTag => "rust";

    public override IReadOnlyList<KeyValuePair<string, bool>>? LanguageFlags => [];

    /// <summary>Registers the cache reconstruction factory so cached Rust statements load back as
    /// <see cref="RustStatementInfo"/> on cache hits. Idempotent.</summary>
    public static void RegisterCacheFactory() =>
        StatementTypeRegistry.Register("rust", (baseDecl, flags) => new RustStatementInfo(baseDecl));
}
