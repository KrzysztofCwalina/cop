// Carries Java type-category and modifier facts through the shared source cache.

namespace Cop.Providers.SourceModel;

/// <summary>
/// A Java-specific <see cref="TypeDeclaration"/> carrying Java-only facts. Records and
/// (to a degree) enums are flattened to <c>Struct</c>/<c>Enum</c> in the common model, so
/// <c>IsRecord</c> recovers the distinction. Only the Java provider emits these; the runtime
/// maps this CLR type to the cop type <c>JavaType</c> so <c>:asJava</c> checks can read them.
/// Round-trips through the source cache via <see cref="LanguageTag"/>/<see cref="LanguageFlags"/>.
/// </summary>
public sealed record JavaTypeDeclaration : TypeDeclaration
{
    /// <summary>
    /// Copies a shared type before Java-specific facts are initialized.
    /// </summary>
    public JavaTypeDeclaration(TypeDeclaration source)
        : base(source)
    {
    }

    /// <summary>True for Java <c>record</c> declarations.</summary>
    public bool IsRecord { get; init; }

    /// <summary>True for Java <c>enum</c> declarations.</summary>
    public bool IsEnum { get; init; }

    /// <summary>
    /// True for the Java sealed keyword, distinct from the common final-type fact.
    /// </summary>
    public bool HasSealedModifier { get; init; }

    /// <summary>True for Java <c>non-sealed</c> declarations.</summary>
    public bool IsNonSealed { get; init; }

    /// <summary>True for Java <c>final</c> declarations.</summary>
    public bool IsFinal { get; init; }

    /// <summary>True for Java generic type declarations.</summary>
    public bool IsGeneric { get; init; }

    /// <inheritdoc />
    public override string? LanguageTag => "java";

    /// <inheritdoc />
    public override IReadOnlyList<KeyValuePair<string, bool>>? LanguageFlags =>
    [
        new("IsRecord", IsRecord),
        new("IsEnum", IsEnum),
        new("IsSealed", HasSealedModifier),
        new("IsNonSealed", IsNonSealed),
        new("IsFinal", IsFinal),
        new("IsGeneric", IsGeneric),
    ];

    /// <summary>
    /// Registers reconstruction of Java type flags from cached declarations.
    /// </summary>
    public static void RegisterCacheFactory() =>
        LanguageTypeRegistry.Register("java", (baseDecl, flags) => new JavaTypeDeclaration(baseDecl)
        {
            IsRecord = flags.TryGetValue("IsRecord", out bool r) && r,
            IsEnum = flags.TryGetValue("IsEnum", out bool e) && e,
            HasSealedModifier = flags.TryGetValue("IsSealed", out bool s) && s,
            IsNonSealed = flags.TryGetValue("IsNonSealed", out bool ns) && ns,
            IsFinal = flags.TryGetValue("IsFinal", out bool f) && f,
            IsGeneric = flags.TryGetValue("IsGeneric", out bool g) && g
        });
}
