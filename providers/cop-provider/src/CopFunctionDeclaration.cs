namespace Cop.Providers.SourceModel;

/// <summary>A Cop function, predicate, or command, with facts taken from its parsed AST.</summary>
public sealed record CopFunctionDeclaration : MethodDeclaration
{
    public CopFunctionDeclaration(MethodDeclaration source, bool isIntrinsic, bool isPredicate,
        bool isCommand, bool hasGuard) : base(source)
    {
        IsIntrinsic = isIntrinsic;
        IsPredicate = isPredicate;
        IsCommand = isCommand;
        HasGuard = hasGuard;
    }

    public bool IsIntrinsic { get; init; }
    public bool IsPredicate { get; init; }
    public bool IsCommand { get; init; }
    public bool HasGuard { get; init; }
    public bool IsExported => IsPublic;

    public override string? LanguageTag => "cop";

    public override IReadOnlyList<KeyValuePair<string, bool>>? LanguageFlags =>
    [
        new("IsIntrinsic", IsIntrinsic),
        new("IsPredicate", IsPredicate),
        new("IsCommand", IsCommand),
        new("HasGuard", HasGuard)
    ];

    public static void RegisterCacheFactory() =>
        MethodTypeRegistry.Register("cop", (source, flags) => new CopFunctionDeclaration(
            source, flags["IsIntrinsic"], flags["IsPredicate"], flags["IsCommand"], flags["HasGuard"]));
}
