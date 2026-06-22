namespace Cop.Providers.SourceModel;

/// <summary>
/// A Python-specific <see cref="TypeDeclaration"/>. The common model marks every Python class
/// as <c>Class</c>, so <c>IsDataclass</c>/<c>IsEnum</c> recover idiomatic distinctions. Only the
/// Python provider emits these; the runtime maps this CLR type to the cop type <c>PythonType</c>
/// so <c>:asPython</c> checks can read them. Round-trips through the source cache.
/// </summary>
public sealed record PythonTypeDeclaration : TypeDeclaration
{
    public PythonTypeDeclaration(TypeDeclaration source, bool isDataclass, bool isEnum,
        bool isAbstract = false, bool isNamedTuple = false, bool isProtocol = false,
        bool isException = false, bool hasSlots = false)
        : base(source)
    {
        IsDataclass = isDataclass;
        IsEnum = isEnum;
        IsAbstract = isAbstract;
        IsNamedTuple = isNamedTuple;
        IsProtocol = isProtocol;
        IsException = isException;
        HasSlots = hasSlots;
    }

    /// <summary>True for classes decorated with <c>@dataclass</c>.</summary>
    public bool IsDataclass { get; init; }

    /// <summary>True for classes deriving from <c>Enum</c> (IntEnum, StrEnum, Flag, …).</summary>
    public bool IsEnum { get; init; }

    /// <summary>True for classes deriving from <c>ABC</c> or using <c>ABCMeta</c>.</summary>
    public bool IsAbstract { get; init; }

    /// <summary>True for classes deriving from <c>NamedTuple</c>.</summary>
    public bool IsNamedTuple { get; init; }

    /// <summary>True for classes deriving from <c>Protocol</c>.</summary>
    public bool IsProtocol { get; init; }

    /// <summary>True for classes deriving from <c>Exception</c>, <c>Error</c>, or <c>BaseException</c>.</summary>
    public bool IsException { get; init; }

    /// <summary>True for classes declaring <c>__slots__</c>.</summary>
    public bool HasSlots { get; init; }

    public override string? LanguageTag => "python";

    public override IReadOnlyList<KeyValuePair<string, bool>>? LanguageFlags =>
    [
        new("IsDataclass", IsDataclass),
        new("IsEnum", IsEnum),
        new("IsAbstract", IsAbstract),
        new("IsNamedTuple", IsNamedTuple),
        new("IsProtocol", IsProtocol),
        new("IsException", IsException),
        new("HasSlots", HasSlots),
    ];

    public static void RegisterCacheFactory() =>
        LanguageTypeRegistry.Register("python", (baseDecl, flags) => new PythonTypeDeclaration(
            baseDecl,
            isDataclass: flags.TryGetValue("IsDataclass", out var d) && d,
            isEnum: flags.TryGetValue("IsEnum", out var e) && e,
            isAbstract: flags.TryGetValue("IsAbstract", out var a) && a,
            isNamedTuple: flags.TryGetValue("IsNamedTuple", out var n) && n,
            isProtocol: flags.TryGetValue("IsProtocol", out var p) && p,
            isException: flags.TryGetValue("IsException", out var x) && x,
            hasSlots: flags.TryGetValue("HasSlots", out var s) && s));
}

public static class PythonTypeDeclarationExtensions
{
    public static PythonTypeDeclaration AsPython(this TypeDeclaration source, bool isDataclass = false, bool isEnum = false,
        bool isAbstract = false, bool isNamedTuple = false, bool isProtocol = false,
        bool isException = false, bool hasSlots = false)
        => new(source, isDataclass, isEnum, isAbstract, isNamedTuple, isProtocol, isException, hasSlots);
}
