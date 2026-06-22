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
    public JavaTypeDeclaration(
        TypeDeclaration source,
        bool isRecord,
        bool isEnum,
        bool isSealed = false,
        bool isNonSealed = false,
        bool isFinal = false,
        bool isGeneric = false)
        : base(source)
    {
        IsRecord = isRecord;
        IsEnum = isEnum;
        IsSealed = isSealed;
        IsNonSealed = isNonSealed;
        IsFinal = isFinal;
        IsGeneric = isGeneric;
    }

    /// <summary>True for Java <c>record</c> declarations.</summary>
    public bool IsRecord { get; init; }

    /// <summary>True for Java <c>enum</c> declarations.</summary>
    public bool IsEnum { get; init; }

    /// <summary>True for Java <c>sealed</c> declarations.</summary>
    public bool IsSealed { get; init; }

    /// <summary>True for Java <c>non-sealed</c> declarations.</summary>
    public bool IsNonSealed { get; init; }

    /// <summary>True for Java <c>final</c> declarations.</summary>
    public bool IsFinal { get; init; }

    /// <summary>True for Java generic type declarations.</summary>
    public bool IsGeneric { get; init; }

    public override string? LanguageTag => "java";

    public override IReadOnlyList<KeyValuePair<string, bool>>? LanguageFlags =>
    [
        new("IsRecord", IsRecord),
        new("IsEnum", IsEnum),
        new("IsSealed", IsSealed),
        new("IsNonSealed", IsNonSealed),
        new("IsFinal", IsFinal),
        new("IsGeneric", IsGeneric),
    ];

    public static void RegisterCacheFactory() =>
        LanguageTypeRegistry.Register("java", (baseDecl, flags) => new JavaTypeDeclaration(
            baseDecl,
            isRecord: flags.TryGetValue("IsRecord", out var r) && r,
            isEnum: flags.TryGetValue("IsEnum", out var e) && e,
            isSealed: flags.TryGetValue("IsSealed", out var s) && s,
            isNonSealed: flags.TryGetValue("IsNonSealed", out var ns) && ns,
            isFinal: flags.TryGetValue("IsFinal", out var f) && f,
            isGeneric: flags.TryGetValue("IsGeneric", out var g) && g));
}

public static class JavaTypeDeclarationExtensions
{
    /// <summary>Wraps a common <see cref="TypeDeclaration"/> as a Java-specific one.</summary>
    public static JavaTypeDeclaration AsJava(
        this TypeDeclaration source,
        bool isRecord = false,
        bool isEnum = false,
        bool isSealed = false,
        bool isNonSealed = false,
        bool isFinal = false,
        bool isGeneric = false)
        => new(source, isRecord, isEnum, isSealed, isNonSealed, isFinal, isGeneric);
}
