// Preserves Java statement-specific facts when storing and restoring source models.

namespace Cop.Providers.SourceModel;

/// <summary>
/// A Java-specific <see cref="StatementInfo"/> carrying Java-only statement facts.
/// All Java statements emitted by the parser use this subtype so <c>:asJava</c> narrowing is stable.
/// </summary>
public sealed class JavaStatementInfo : StatementInfo
{
    internal int SyntaxStart { get; set; } = -1;
    internal int SyntaxEnd { get; set; } = -1;

    /// <summary>
    /// Creates a Java statement from parsed syntax and source-location facts.
    /// </summary>
    public JavaStatementInfo(StatementInfo source) : base(source) { }

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

    /// <summary>True when every supported explicit catch path throws the original binding.</summary>
    public bool IsOriginalExceptionAlwaysRethrown { get; set; }

    /// <summary>True for an invocation directly initializing a try-with-resources variable.</summary>
    public bool IsUsingResource { get; set; }

    /// <summary>True for a created object directly returned or stored in a bound field.</summary>
    public bool IsOwnershipTransferred { get; set; }

    internal bool QuerySemanticsAvailable { get; set; }
    internal bool CancellationHandler { get; set; }
    internal bool CallerCancellationPreserved { get; set; }

    /// <summary>True for a compiler-bound InterruptedException catch or subtype.</summary>
    public bool IsCancellationHandler
    {
        get { WarnIfUnavailable(); return CancellationHandler; }
    }

    /// <summary>True for supported explicit propagation or restoration of interruption.</summary>
    public bool IsCallerCancellationPreserved
    {
        get { WarnIfUnavailable(); return CallerCancellationPreserved; }
    }

    internal void WarnIfUnavailable()
    {
        if (!QuerySemanticsAvailable)
        {
            Cop.Core.ProviderWarnings.ReportOnce("java", "query-semantics",
                "Java interruption and bound field-ownership proofs require JDK attribution; unavailable facts are false. Direct return/resource and original-rethrow syntax proofs remain available.");
        }
    }

    /// <inheritdoc />
    public override string? LanguageTag => "java";

    /// <inheritdoc />
    public override IReadOnlyList<KeyValuePair<string, bool>>? LanguageFlags =>
    [
        new("IsTryWithResources", IsTryWithResources),
        new("IsEnhancedFor", IsEnhancedFor),
        new("IsOriginalExceptionAlwaysRethrown", IsOriginalExceptionAlwaysRethrown),
        new("IsUsingResource", IsUsingResource),
        new("IsOwnershipTransferred", IsOwnershipTransferred),
        new("QuerySemanticsAvailable", QuerySemanticsAvailable),
        new("IsCancellationHandler", CancellationHandler),
        new("IsCallerCancellationPreserved", CallerCancellationPreserved),
    ];

    /// <summary>
    /// Registers restoration of Java statements from cached source facts.
    /// </summary>
    public static void RegisterCacheFactory() =>
        StatementTypeRegistry.Register("java", (baseDecl, flags) => new JavaStatementInfo(baseDecl)
        {
            IsTryWithResources = flags.TryGetValue("IsTryWithResources", out bool tryWithResources) && tryWithResources,
            IsEnhancedFor = flags.TryGetValue("IsEnhancedFor", out bool enhancedFor) && enhancedFor,
            IsOriginalExceptionAlwaysRethrown = flags.GetValueOrDefault("IsOriginalExceptionAlwaysRethrown"),
            IsUsingResource = flags.GetValueOrDefault("IsUsingResource"),
            IsOwnershipTransferred = flags.GetValueOrDefault("IsOwnershipTransferred"),
            QuerySemanticsAvailable = flags.GetValueOrDefault("QuerySemanticsAvailable"),
            CancellationHandler = flags.GetValueOrDefault("IsCancellationHandler"),
            CallerCancellationPreserved = flags.GetValueOrDefault("IsCallerCancellationPreserved"),
        });
}
