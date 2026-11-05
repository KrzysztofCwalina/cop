// Preserves Cop function kinds and guards in the shared method model and cache.

namespace Cop.Providers.SourceModel;

/// <summary>A Cop function, predicate, or command, with facts taken from its parsed AST.</summary>
public sealed record CopFunctionDeclaration : MethodDeclaration
{
    /// <summary>
    /// Copies a method declaration and attaches parsed Cop function facts.
    /// </summary>
    public CopFunctionDeclaration(MethodDeclaration source, bool isIntrinsic, bool isPredicate,
        bool isCommand, bool hasGuard) : base(source)
    {
        IsIntrinsic = isIntrinsic;
        IsPredicate = isPredicate;
        IsCommand = isCommand;
        HasGuard = hasGuard;
    }

    /// <summary>Gets whether the declaration delegates to a runtime intrinsic.</summary>
    public bool IsIntrinsic { get; init; }
    /// <summary>Gets whether the declaration is a predicate.</summary>
    public bool IsPredicate { get; init; }
    /// <summary>Gets whether the declaration is a runnable command.</summary>
    public bool IsCommand { get; init; }
    /// <summary>Gets whether the declaration includes an input guard.</summary>
    public bool HasGuard { get; init; }
    /// <summary>Gets whether the declaration is exported from its package.</summary>
    public bool IsExported => IsPublic;

    /// <inheritdoc />
    public override string? LanguageTag => "cop";

    /// <inheritdoc />
    public override IReadOnlyList<KeyValuePair<string, bool>>? LanguageFlags =>
    [
        new("IsIntrinsic", IsIntrinsic),
        new("IsPredicate", IsPredicate),
        new("IsCommand", IsCommand),
        new("HasGuard", HasGuard)
    ];

    /// <summary>
    /// Registers reconstruction of Cop function facts from the source cache.
    /// </summary>
    public static void RegisterCacheFactory() =>
        MethodTypeRegistry.Register("cop", (source, flags) => new CopFunctionDeclaration(
            source, flags["IsIntrinsic"], flags["IsPredicate"], flags["IsCommand"], flags["HasGuard"]));
}
