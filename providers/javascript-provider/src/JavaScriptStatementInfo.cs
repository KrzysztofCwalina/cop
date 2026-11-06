// Restores JavaScript-specific statement identities when reading cached source models.

namespace Cop.Providers.SourceModel;

/// <summary>
/// A JavaScript/TypeScript-specific <see cref="StatementInfo"/> exposing JS statement kinds.
/// All JS statements emitted by the parser use this subtype so <c>:asJavaScript</c> narrowing is stable.
/// </summary>
public sealed class JavaScriptStatementInfo : StatementInfo
{
    /// <summary>
    /// Creates a JavaScript statement from parsed syntax and source facts.
    /// </summary>
    public JavaScriptStatementInfo(StatementInfo source) : base(source) { }

    /// <summary>True for a JavaScript <c>for (... of ...)</c> statement.</summary>
    public bool IsForOf => Kind == "for-of";

    /// <summary>True for a JavaScript <c>for (... in ...)</c> statement.</summary>
    public bool IsForIn => Kind == "for-in";

    /// <summary>True for a JavaScript <c>throw</c> statement.</summary>
    public bool IsThrow => Kind == "throw";

    /// <summary>True for a JavaScript <c>await</c> statement/expression.</summary>
    public bool IsAwait => Kind == "await";

    /// <summary>True for a JavaScript <c>try</c> statement.</summary>
    public bool IsTryCatch => Kind == "try";

    /// <inheritdoc />
    public override string? LanguageTag => "javascript";

    /// <inheritdoc />
    public override IReadOnlyList<KeyValuePair<string, bool>>? LanguageFlags => [];

    /// <summary>
    /// Registers restoration of JavaScript statements from cached source facts.
    /// </summary>
    public static void RegisterCacheFactory() =>
        StatementTypeRegistry.Register("javascript", (baseDecl, _) => new JavaScriptStatementInfo(baseDecl));
}
