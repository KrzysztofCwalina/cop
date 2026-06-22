namespace Cop.Providers.SourceModel;

/// <summary>
/// A JavaScript/TypeScript-specific <see cref="MethodDeclaration"/> carrying JS-only method facts.
/// Round-trips through the source cache via <see cref="LanguageTag"/>/<see cref="LanguageFlags"/>.
/// </summary>
public sealed record JavaScriptMethodDeclaration : MethodDeclaration
{
    public JavaScriptMethodDeclaration(
        MethodDeclaration source,
        bool isGenerator = false,
        bool isArrow = false,
        bool isGetter = false,
        bool isSetter = false)
        : base(source)
    {
        IsGenerator = isGenerator;
        IsArrow = isArrow;
        IsGetter = isGetter;
        IsSetter = isSetter;
    }

    /// <summary>True for <c>function*</c> or <c>*name()</c> generator methods.</summary>
    public bool IsGenerator { get; init; }

    /// <summary>True for class-field arrow function methods.</summary>
    public bool IsArrow { get; init; }

    /// <summary>True for <c>get name()</c> accessors.</summary>
    public bool IsGetter { get; init; }

    /// <summary>True for <c>set name(value)</c> accessors.</summary>
    public bool IsSetter { get; init; }

    /// <summary>True for JavaScript/TypeScript class constructors.</summary>
    public bool IsConstructor => Name == "constructor";

    public override string? LanguageTag => "javascript";

    public override IReadOnlyList<KeyValuePair<string, bool>>? LanguageFlags =>
    [
        new("IsGenerator", IsGenerator),
        new("IsArrow", IsArrow),
        new("IsGetter", IsGetter),
        new("IsSetter", IsSetter),
    ];

    public static void RegisterCacheFactory() =>
        MethodTypeRegistry.Register("javascript", (baseDecl, flags) => new JavaScriptMethodDeclaration(
            baseDecl,
            isGenerator: flags.TryGetValue("IsGenerator", out var generator) && generator,
            isArrow: flags.TryGetValue("IsArrow", out var arrow) && arrow,
            isGetter: flags.TryGetValue("IsGetter", out var getter) && getter,
            isSetter: flags.TryGetValue("IsSetter", out var setter) && setter));
}

public static class JavaScriptMethodDeclarationExtensions
{
    public static JavaScriptMethodDeclaration AsJavaScript(
        this MethodDeclaration source,
        bool isGenerator = false,
        bool isArrow = false,
        bool isGetter = false,
        bool isSetter = false)
        => new(source, isGenerator, isArrow, isGetter, isSetter);
}
