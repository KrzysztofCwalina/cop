namespace Cop.Providers.SourceModel;

/// <summary>
/// A Python-specific <see cref="TypeDeclaration"/>. The common model marks every Python class
/// as <c>Class</c>, so <c>IsDataclass</c>/<c>IsEnum</c> recover idiomatic distinctions. Only the
/// Python provider emits these; the runtime maps this CLR type to the cop type <c>PythonType</c>
/// so <c>:asPython</c> checks can read them. Round-trips through the source cache.
/// </summary>
public sealed record PythonTypeDeclaration : TypeDeclaration
{
    public PythonTypeDeclaration(TypeDeclaration source, bool isDataclass, bool isEnum)
        : base(source)
    {
        IsDataclass = isDataclass;
        IsEnum = isEnum;
    }

    /// <summary>True for classes decorated with <c>@dataclass</c>.</summary>
    public bool IsDataclass { get; init; }

    /// <summary>True for classes deriving from <c>Enum</c> (IntEnum, StrEnum, Flag, …).</summary>
    public bool IsEnum { get; init; }

    public override string? LanguageTag => "python";

    public override IReadOnlyList<KeyValuePair<string, bool>>? LanguageFlags =>
    [
        new("IsDataclass", IsDataclass),
        new("IsEnum", IsEnum),
    ];

    public static void RegisterCacheFactory() =>
        LanguageTypeRegistry.Register("python", (baseDecl, flags) => new PythonTypeDeclaration(
            baseDecl,
            isDataclass: flags.TryGetValue("IsDataclass", out var d) && d,
            isEnum: flags.TryGetValue("IsEnum", out var e) && e));
}

public static class PythonTypeDeclarationExtensions
{
    public static PythonTypeDeclaration AsPython(this TypeDeclaration source, bool isDataclass = false, bool isEnum = false)
        => new(source, isDataclass, isEnum);
}
