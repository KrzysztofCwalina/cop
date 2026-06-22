namespace Cop.Providers.SourceModel;

/// <summary>
/// A JavaScript/TypeScript-specific <see cref="StatementInfo"/> exposing JS statement kinds.
/// All JS statements emitted by the parser use this subtype so <c>:asJavaScript</c> narrowing is stable.
/// </summary>
public sealed class JavaScriptStatementInfo : StatementInfo
{
    public JavaScriptStatementInfo(
        string kind, List<string> keywords, string? typeName, string? memberName,
        List<string> arguments, int line, bool isInMethod)
        : base(kind, keywords, typeName, memberName, arguments, line, isInMethod) { }

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

    public override string? LanguageTag => "javascript";

    public override IReadOnlyList<KeyValuePair<string, bool>>? LanguageFlags => [];

    public static void RegisterCacheFactory() =>
        StatementTypeRegistry.Register("javascript", (baseDecl, _) => new JavaScriptStatementInfo(
            baseDecl.Kind,
            baseDecl.Keywords,
            baseDecl.TypeName,
            baseDecl.MemberName,
            baseDecl.Arguments,
            baseDecl.Line,
            baseDecl.IsInMethod)
        {
            HasRethrow = baseDecl.HasRethrow,
            IsErrorHandler = baseDecl.IsErrorHandler,
            IsGenericErrorHandler = baseDecl.IsGenericErrorHandler,
            IsBraced = baseDecl.IsBraced,
            CopIgnore = baseDecl.CopIgnore,
            Condition = baseDecl.Condition,
            Expression = baseDecl.Expression,
            _children = baseDecl._children,
            ConstructedTypeInterfaces = baseDecl.ConstructedTypeInterfaces,
        });
}
