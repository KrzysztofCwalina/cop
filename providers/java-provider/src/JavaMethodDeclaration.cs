// Preserves Java method modifiers and generic facts through source caching.

namespace Cop.Providers.SourceModel;

/// <summary>
/// A Java-specific <see cref="MethodDeclaration"/> carrying Java-only method facts.
/// Round-trips through the source cache via <see cref="LanguageTag"/>/<see cref="LanguageFlags"/>.
/// </summary>
public sealed record JavaMethodDeclaration : MethodDeclaration
{
    /// <summary>
    /// Copies a shared method; Java-specific facts are supplied through initializers.
    /// </summary>
    public JavaMethodDeclaration(MethodDeclaration source)
        : base(source)
    {
    }

    /// <summary>True for Java <c>synchronized</c> method declarations.</summary>
    public bool IsSynchronized { get; init; }

    /// <summary>True for Java <c>native</c> method declarations.</summary>
    public bool IsNative { get; init; }

    /// <summary>True for Java interface <c>default</c> method declarations.</summary>
    public bool IsDefault { get; init; }

    /// <summary>True for Java <c>strictfp</c> method declarations.</summary>
    public bool IsStrictfp { get; init; }

    /// <summary>True when the method declares generic type parameters.</summary>
    public bool IsGeneric { get; init; }

    /// <inheritdoc />
    public override string? LanguageTag => "java";

    /// <inheritdoc />
    public override IReadOnlyList<KeyValuePair<string, bool>>? LanguageFlags =>
    [
        new("IsSynchronized", IsSynchronized),
        new("IsNative", IsNative),
        new("IsDefault", IsDefault),
        new("IsStrictfp", IsStrictfp),
        new("IsGeneric", IsGeneric),
    ];

    /// <summary>
    /// Registers reconstruction of Java method flags from cached declarations.
    /// </summary>
    public static void RegisterCacheFactory() =>
        MethodTypeRegistry.Register("java", (baseDecl, flags) => new JavaMethodDeclaration(baseDecl)
        {
            IsSynchronized = flags.TryGetValue("IsSynchronized", out bool sync) && sync,
            IsNative = flags.TryGetValue("IsNative", out bool native) && native,
            IsDefault = flags.TryGetValue("IsDefault", out bool def) && def,
            IsStrictfp = flags.TryGetValue("IsStrictfp", out bool strictfp) && strictfp,
            IsGeneric = flags.TryGetValue("IsGeneric", out bool generic) && generic
        });
}
