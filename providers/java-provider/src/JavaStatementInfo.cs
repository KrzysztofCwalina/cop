// Preserves Java statement-specific facts when storing and restoring source models.

namespace Cop.Providers.SourceModel;

/// <summary>
/// A Java-specific <see cref="StatementInfo"/> carrying Java-only statement facts.
/// All Java statements emitted by the parser use this subtype so <c>:asJava</c> narrowing is stable.
/// </summary>
public sealed class JavaStatementInfo : StatementInfo
{
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

    /// <inheritdoc />
    public override string? LanguageTag => "java";

    /// <inheritdoc />
    public override IReadOnlyList<KeyValuePair<string, bool>>? LanguageFlags =>
    [
        new("IsTryWithResources", IsTryWithResources),
        new("IsEnhancedFor", IsEnhancedFor),
    ];

    /// <summary>
    /// Registers restoration of Java statements from cached source facts.
    /// </summary>
    public static void RegisterCacheFactory() =>
        StatementTypeRegistry.Register("java", (baseDecl, flags) => new JavaStatementInfo(baseDecl)
        {
            IsTryWithResources = flags.TryGetValue("IsTryWithResources", out bool tryWithResources) && tryWithResources,
            IsEnhancedFor = flags.TryGetValue("IsEnhancedFor", out bool enhancedFor) && enhancedFor,
        });
}
