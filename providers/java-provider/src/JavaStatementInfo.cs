namespace Cop.Providers.SourceModel;

/// <summary>
/// A Java-specific <see cref="StatementInfo"/> carrying Java-only statement facts.
/// All Java statements emitted by the parser use this subtype so <c>:asJava</c> narrowing is stable.
/// </summary>
public sealed class JavaStatementInfo : StatementInfo
{
    public JavaStatementInfo(
        string kind, List<string> keywords, string? typeName, string? memberName,
        List<string> arguments, int line, bool isInMethod)
        : base(kind, keywords, typeName, memberName, arguments, line, isInMethod) { }

    /// <summary>True for a Java <c>synchronized</c> block.</summary>
    public bool IsSynchronized => Kind == "synchronized";

    /// <summary>True for a Java <c>try</c> statement with resources.</summary>
    public bool IsTryWithResources { get; init; }

    /// <summary>True for an enhanced <c>for</c> / for-each statement.</summary>
    public bool IsEnhancedFor { get; init; }

    /// <summary>True for a Java <c>throw</c> statement.</summary>
    public bool IsThrow => Kind == "throw";

    /// <summary>True for a Java <c>assert</c> statement.</summary>
    public bool IsAssert => Kind == "assert";

    public override string? LanguageTag => "java";

    public override IReadOnlyList<KeyValuePair<string, bool>>? LanguageFlags =>
    [
        new("IsTryWithResources", IsTryWithResources),
        new("IsEnhancedFor", IsEnhancedFor),
    ];

    public static void RegisterCacheFactory() =>
        StatementTypeRegistry.Register("java", (baseDecl, flags) => new JavaStatementInfo(
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
            Condition = baseDecl.Condition,
            Expression = baseDecl.Expression,
            CopIgnore = baseDecl.CopIgnore,
            _children = baseDecl._children,
            IsTryWithResources = flags.TryGetValue("IsTryWithResources", out var tryWithResources) && tryWithResources,
            IsEnhancedFor = flags.TryGetValue("IsEnhancedFor", out var enhancedFor) && enhancedFor,
        });
}
