// Preserves JavaScript function-form facts through the shared method cache.

namespace Cop.Providers.SourceModel;

/// <summary>
/// A JavaScript/TypeScript-specific <see cref="MethodDeclaration"/> carrying JS-only method facts.
/// Round-trips through the source cache via <see cref="LanguageTag"/>/<see cref="LanguageFlags"/>.
/// </summary>
public sealed record JavaScriptMethodDeclaration : MethodDeclaration
{
    /// <summary>Copies a shared method and adds JavaScript function-form flags.</summary>
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

    /// <summary>True for arrow functions, including module functions and class-field methods.</summary>
    public bool IsArrow { get; init; }

    /// <summary>True for <c>get name()</c> accessors.</summary>
    public bool IsGetter { get; init; }

    /// <summary>True for <c>set name(value)</c> accessors.</summary>
    public bool IsSetter { get; init; }

    /// <summary>True for JavaScript/TypeScript class constructors.</summary>
    public bool IsConstructor => Name == "constructor";

    /// <inheritdoc />
    public override string? LanguageTag => "javascript";

    /// <inheritdoc />
    public override IReadOnlyList<KeyValuePair<string, bool>>? LanguageFlags =>
    [
        new("IsGenerator", IsGenerator),
        new("IsArrow", IsArrow),
        new("IsGetter", IsGetter),
        new("IsSetter", IsSetter),
    ];

    /// <summary>
    /// Registers restoration of JavaScript method facts from source caches.
    /// </summary>
    public static void RegisterCacheFactory() =>
        MethodTypeRegistry.Register("javascript", (baseDecl, flags) => new JavaScriptMethodDeclaration(
            baseDecl,
            isGenerator: flags.TryGetValue("IsGenerator", out bool generator) && generator,
            isArrow: flags.TryGetValue("IsArrow", out bool arrow) && arrow,
            isGetter: flags.TryGetValue("IsGetter", out bool getter) && getter,
            isSetter: flags.TryGetValue("IsSetter", out bool setter) && setter));
}
