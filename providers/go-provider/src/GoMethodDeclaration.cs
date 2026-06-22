using Cop.Providers;

namespace Cop.Providers.SourceModel;

/// <summary>
/// A Go-specific <see cref="MethodDeclaration"/> carrying Go-only function/method facts.
/// The Go provider emits this for every Go function signature it models, so narrowing with
/// <c>:asGo</c> exposes these fields while the common method model stays language-neutral.
/// </summary>
public sealed record GoMethodDeclaration : MethodDeclaration
{
    public GoMethodDeclaration(
        MethodDeclaration source,
        bool isPointerReceiver = false,
        bool hasNamedReturns = false,
        bool isVariadic = false,
        bool isGeneric = false)
        : base(source)
    {
        IsPointerReceiver = isPointerReceiver;
        HasNamedReturns = hasNamedReturns;
        IsVariadic = isVariadic;
        IsGeneric = isGeneric;
    }

    /// <summary>True for methods whose receiver is a pointer, e.g. <c>(*T)</c>.</summary>
    public bool IsPointerReceiver { get; init; }

    /// <summary>True when the function declares named return parameters.</summary>
    public bool HasNamedReturns { get; init; }

    /// <summary>True when the final parameter is variadic (<c>...T</c>).</summary>
    public bool IsVariadic { get; init; }

    /// <summary>True when the function declares type parameters.</summary>
    public bool IsGeneric { get; init; }

    public override string? LanguageTag => "go";

    public override IReadOnlyList<KeyValuePair<string, bool>>? LanguageFlags =>
    [
        new("IsPointerReceiver", IsPointerReceiver),
        new("HasNamedReturns", HasNamedReturns),
        new("IsVariadic", IsVariadic),
        new("IsGeneric", IsGeneric),
    ];

    public static void RegisterCacheFactory() =>
        MethodTypeRegistry.Register("go", (baseDecl, flags) => new GoMethodDeclaration(
            baseDecl,
            isPointerReceiver: flags.TryGetValue("IsPointerReceiver", out var pointerReceiver) && pointerReceiver,
            hasNamedReturns: flags.TryGetValue("HasNamedReturns", out var namedReturns) && namedReturns,
            isVariadic: flags.TryGetValue("IsVariadic", out var variadic) && variadic,
            isGeneric: flags.TryGetValue("IsGeneric", out var generic) && generic));
}

public static class GoMethodDeclarationExtensions
{
    public static GoMethodDeclaration AsGo(
        this MethodDeclaration source,
        bool isPointerReceiver = false,
        bool hasNamedReturns = false,
        bool isVariadic = false,
        bool isGeneric = false)
        => new(source, isPointerReceiver, hasNamedReturns, isVariadic, isGeneric);
}
