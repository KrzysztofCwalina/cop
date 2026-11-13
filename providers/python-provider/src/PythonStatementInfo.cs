// Exposes Python statement categories and preserves them through source caching.

namespace Cop.Providers.SourceModel;

/// <summary>
/// A Python-specific <see cref="StatementInfo"/> exposing Python statement kinds. The Python
/// provider emits this for every Python statement so <c>:asPython</c> narrowing is consistent.
/// </summary>
public sealed class PythonStatementInfo : StatementInfo
{
    /// <summary>Creates a Python statement from parsed syntax and source facts.</summary>
    public PythonStatementInfo(StatementInfo source) : base(source) { }

    /// <summary>Gets whether this is a synchronous context-manager statement.</summary>
    public bool IsWith => Kind == "with";
    /// <summary>Gets whether this is an asynchronous context-manager statement.</summary>
    public bool IsAsyncWith => Kind == "async with";
    /// <summary>Gets whether this is a Python raise statement.</summary>
    public bool IsRaise => Kind == "throw";
    /// <summary>Gets whether this is a Python assertion statement.</summary>
    public bool IsAssert => Kind == "assert";
    /// <summary>Gets whether this statement models a comprehension.</summary>
    public bool IsComprehension => Kind == "comprehension";
    /// <summary>Gets whether this is a global binding declaration.</summary>
    public bool IsGlobal => Kind == "global";
    /// <summary>Gets whether this is a nonlocal binding declaration.</summary>
    public bool IsNonlocal => Kind == "nonlocal";
    /// <summary>Gets whether this is a yield or yield-from expression.</summary>
    public bool IsYield => Kind == "yield";
    /// <summary>Gets whether this is an asynchronous for loop.</summary>
    public bool IsAsyncFor { get; set; }

    /// <summary>Gets whether this call directly supplies a context-manager expression.</summary>
    public bool IsUsingResource { get; set; }
    /// <summary>Gets whether this call result directly escapes through return or an instance member.</summary>
    public bool IsOwnershipTransferred { get; set; }
    /// <summary>Gets whether every supported explicit handler path raises the original exception.</summary>
    public bool IsOriginalExceptionAlwaysRethrown { get; set; }
    /// <summary>Gets whether this handler names a standard-library cancellation exception.</summary>
    public bool IsCancellationHandler { get; set; }
    /// <summary>Gets whether supported explicit handler paths preserve the original cancellation.</summary>
    public bool IsCallerCancellationPreserved { get; set; }
    /// <summary>Gets whether parsed syntax accesses the standard os environment API.</summary>
    public bool IsEnvironmentVariableAccess { get; set; }
    /// <summary>Gets whether parsed syntax reads a value using getenv, environ.get or subscripting.</summary>
    public bool IsEnvironmentVariableRead { get; set; }

    /// <inheritdoc />
    public override string? LanguageTag => "python";

    /// <inheritdoc />
    public override IReadOnlyList<KeyValuePair<string, bool>>? LanguageFlags =>
    [
        new(nameof(IsAsyncFor), IsAsyncFor),
        new(nameof(IsUsingResource), IsUsingResource),
        new(nameof(IsOwnershipTransferred), IsOwnershipTransferred),
        new(nameof(IsOriginalExceptionAlwaysRethrown), IsOriginalExceptionAlwaysRethrown),
        new(nameof(IsCancellationHandler), IsCancellationHandler),
        new(nameof(IsCallerCancellationPreserved), IsCallerCancellationPreserved),
        new(nameof(IsEnvironmentVariableAccess), IsEnvironmentVariableAccess),
        new(nameof(IsEnvironmentVariableRead), IsEnvironmentVariableRead),
    ];

    /// <summary>
    /// Registers restoration of Python statements from cached source facts.
    /// </summary>
    public static void RegisterCacheFactory() =>
        StatementTypeRegistry.Register("python", (baseDecl, flags) => new PythonStatementInfo(baseDecl)
        {
            IsAsyncFor = flags.GetValueOrDefault(nameof(IsAsyncFor)),
            IsUsingResource = flags.GetValueOrDefault(nameof(IsUsingResource)),
            IsOwnershipTransferred = flags.GetValueOrDefault(nameof(IsOwnershipTransferred)),
            IsOriginalExceptionAlwaysRethrown = flags.GetValueOrDefault(nameof(IsOriginalExceptionAlwaysRethrown)),
            IsCancellationHandler = flags.GetValueOrDefault(nameof(IsCancellationHandler)),
            IsCallerCancellationPreserved = flags.GetValueOrDefault(nameof(IsCallerCancellationPreserved)),
            IsEnvironmentVariableAccess = flags.GetValueOrDefault(nameof(IsEnvironmentVariableAccess)),
            IsEnvironmentVariableRead = flags.GetValueOrDefault(nameof(IsEnvironmentVariableRead)),
        });
}
