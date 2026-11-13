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

    /// <summary>True for a JavaScript yield expression.</summary>
    public bool IsYield => Kind == "yield";

    /// <summary>True for a JavaScript for await (... of ...) loop.</summary>
    public bool IsAwaitForeach => Kind == "for-of" && Keywords.Contains("await");

    /// <summary>True for a parsed process.env access, including computed keys.</summary>
    public bool IsEnvironmentVariableAccess { get; init; }

    /// <summary>True for a process.env access that reads rather than only writes or deletes.</summary>
    public bool IsEnvironmentVariableRead { get; init; }

    /// <summary>True when all supported explicit catch paths throw the unchanged caught value.</summary>
    public bool IsOriginalExceptionAlwaysRethrown { get; init; }

    /// <summary>True for a catch starting with a positive caught-value AbortError name guard.</summary>
    public bool IsCancellationHandler { get; init; }

    /// <summary>True when the recognized AbortError branch always throws the unchanged caught value.</summary>
    public bool IsCallerCancellationPreserved { get; init; }

    /// <summary>True for a construction directly returned or assigned to a this member.</summary>
    public bool IsOwnershipTransferred { get; init; }

    /// <summary>True for a call directly initializing a using or await using declaration.</summary>
    public bool IsUsingResource { get; init; }

    /// <inheritdoc />
    public override string? LanguageTag => "javascript";

    /// <inheritdoc />
    public override IReadOnlyList<KeyValuePair<string, bool>>? LanguageFlags =>
    [
        new("IsEnvironmentVariableAccess", IsEnvironmentVariableAccess),
        new("IsEnvironmentVariableRead", IsEnvironmentVariableRead),
        new("IsOriginalExceptionAlwaysRethrown", IsOriginalExceptionAlwaysRethrown),
        new("IsCancellationHandler", IsCancellationHandler),
        new("IsCallerCancellationPreserved", IsCallerCancellationPreserved),
        new("IsOwnershipTransferred", IsOwnershipTransferred),
        new("IsUsingResource", IsUsingResource)
    ];

    /// <summary>
    /// Registers restoration of JavaScript statements from cached source facts.
    /// </summary>
    public static void RegisterCacheFactory() =>
        StatementTypeRegistry.Register("javascript", (baseDecl, flags) => new JavaScriptStatementInfo(baseDecl)
        {
            IsEnvironmentVariableAccess = flags.GetValueOrDefault("IsEnvironmentVariableAccess"),
            IsEnvironmentVariableRead = flags.GetValueOrDefault("IsEnvironmentVariableRead"),
            IsOriginalExceptionAlwaysRethrown = flags.GetValueOrDefault("IsOriginalExceptionAlwaysRethrown"),
            IsCancellationHandler = flags.GetValueOrDefault("IsCancellationHandler"),
            IsCallerCancellationPreserved = flags.GetValueOrDefault("IsCallerCancellationPreserved"),
            IsOwnershipTransferred = flags.GetValueOrDefault("IsOwnershipTransferred"),
            IsUsingResource = flags.GetValueOrDefault("IsUsingResource")
        });
}
