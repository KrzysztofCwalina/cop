// Preserves Python type-category facts through shared types and source caching.

namespace Cop.Providers.SourceModel;

/// <summary>
/// A Python-specific <see cref="TypeDeclaration"/>. The common model marks every Python class
/// as <c>Class</c>, so <c>IsDataclass</c>/<c>IsEnum</c> recover idiomatic distinctions. Only the
/// Python provider emits these; the runtime maps this CLR type to the cop type <c>PythonType</c>
/// so <c>:asPython</c> checks can read them. Round-trips through the source cache.
/// </summary>
public sealed record PythonTypeDeclaration : TypeDeclaration
{
    /// <summary>
    /// Copies a shared type and preserves additive abstract-modifier facts.
    /// </summary>
    public PythonTypeDeclaration(TypeDeclaration source, bool isAbstract = false)
        : base(source)
    {
        if (isAbstract)
        {
            Modifiers |= Modifier.Abstract;
        }

    }

    /// <summary>True for classes decorated with <c>@dataclass</c>.</summary>
    public bool IsDataclass { get; init; }

    /// <summary>True for classes deriving from <c>Enum</c> (IntEnum, StrEnum, Flag, …).</summary>
    public bool IsEnum { get; init; }

    /// <summary>True for classes deriving from <c>NamedTuple</c>.</summary>
    public bool IsNamedTuple { get; init; }

    /// <summary>True for classes deriving from <c>Protocol</c>.</summary>
    public bool IsProtocol { get; init; }

    /// <summary>True for classes deriving from <c>Exception</c>, <c>Error</c>, or <c>BaseException</c>.</summary>
    public bool IsException { get; init; }

    /// <summary>True for classes declaring <c>__slots__</c>.</summary>
    public bool HasSlots { get; init; }

    /// <inheritdoc />
    public override string? LanguageTag => "python";

    /// <inheritdoc />
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

    /// <summary>Registers restoration of Python type facts from source caches.</summary>
    public static void RegisterCacheFactory() =>
        LanguageTypeRegistry.Register("python", (baseDecl, flags) => new PythonTypeDeclaration(
            baseDecl, isAbstract: flags.TryGetValue("IsAbstract", out bool a) && a)
        {
            IsDataclass = flags.TryGetValue("IsDataclass", out bool d) && d,
            IsEnum = flags.TryGetValue("IsEnum", out bool e) && e,
            IsNamedTuple = flags.TryGetValue("IsNamedTuple", out bool n) && n,
            IsProtocol = flags.TryGetValue("IsProtocol", out bool p) && p,
            IsException = flags.TryGetValue("IsException", out bool x) && x,
            HasSlots = flags.TryGetValue("HasSlots", out bool s) && s
        });
}
