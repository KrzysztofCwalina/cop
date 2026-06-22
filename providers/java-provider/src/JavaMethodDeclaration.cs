namespace Cop.Providers.SourceModel;

/// <summary>
/// A Java-specific <see cref="MethodDeclaration"/> carrying Java-only method facts.
/// Round-trips through the source cache via <see cref="LanguageTag"/>/<see cref="LanguageFlags"/>.
/// </summary>
public sealed record JavaMethodDeclaration : MethodDeclaration
{
    public JavaMethodDeclaration(
        string name,
        Modifier modifiers,
        List<string> decorators,
        TypeReference? returnType,
        List<ParameterDeclaration> parameters,
        int line,
        bool isSynchronized = false,
        bool isNative = false,
        bool isDefault = false,
        bool isStrictfp = false,
        bool isGeneric = false)
        : base(name, modifiers, decorators, returnType, parameters, line)
    {
        IsSynchronized = isSynchronized;
        IsNative = isNative;
        IsDefault = isDefault;
        IsStrictfp = isStrictfp;
        IsGeneric = isGeneric;
    }

    public JavaMethodDeclaration(
        MethodDeclaration source,
        bool isSynchronized = false,
        bool isNative = false,
        bool isDefault = false,
        bool isStrictfp = false,
        bool isGeneric = false)
        : base(source)
    {
        IsSynchronized = isSynchronized;
        IsNative = isNative;
        IsDefault = isDefault;
        IsStrictfp = isStrictfp;
        IsGeneric = isGeneric;
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

    public override string? LanguageTag => "java";

    public override IReadOnlyList<KeyValuePair<string, bool>>? LanguageFlags =>
    [
        new("IsSynchronized", IsSynchronized),
        new("IsNative", IsNative),
        new("IsDefault", IsDefault),
        new("IsStrictfp", IsStrictfp),
        new("IsGeneric", IsGeneric),
    ];

    public static void RegisterCacheFactory() =>
        MethodTypeRegistry.Register("java", (baseDecl, flags) => new JavaMethodDeclaration(
            baseDecl,
            isSynchronized: flags.TryGetValue("IsSynchronized", out var sync) && sync,
            isNative: flags.TryGetValue("IsNative", out var native) && native,
            isDefault: flags.TryGetValue("IsDefault", out var def) && def,
            isStrictfp: flags.TryGetValue("IsStrictfp", out var strictfp) && strictfp,
            isGeneric: flags.TryGetValue("IsGeneric", out var generic) && generic));
}

public static class JavaMethodDeclarationExtensions
{
    /// <summary>Wraps a common <see cref="MethodDeclaration"/> as a Java-specific one.</summary>
    public static JavaMethodDeclaration AsJava(
        this MethodDeclaration source,
        bool isSynchronized = false,
        bool isNative = false,
        bool isDefault = false,
        bool isStrictfp = false,
        bool isGeneric = false)
        => new(source, isSynchronized, isNative, isDefault, isStrictfp, isGeneric);
}
