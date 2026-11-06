// Preserves Go statement kinds when reconstructing shared statements from cache.

using Cop.Providers;

namespace Cop.Providers.SourceModel;

/// <summary>
/// A Go-specific <see cref="StatementInfo"/>. Its current Go-only facts are derived from
/// parser-emitted statement kinds, so no extra flags are needed for cache round-tripping.
/// </summary>
public sealed class GoStatementInfo : StatementInfo
{
    /// <summary>
    /// Creates a Go statement from parsed syntax and source-location facts.
    /// </summary>
    public GoStatementInfo(StatementInfo source) : base(source) { }

    /// <summary>True for a Go <c>defer</c> statement.</summary>
    public bool IsDefer => Kind == "defer";

    /// <summary>True for a Go <c>go</c> statement that starts a goroutine.</summary>
    public bool IsGoroutine => Kind == "go";

    /// <summary>True for a Go <c>select</c> statement.</summary>
    public bool IsSelect => Kind == "select";

    /// <summary>True for a Go <c>for ... range</c> loop.</summary>
    public bool IsRangeLoop => Kind == "range";

    /// <summary>True for a Go type switch.</summary>
    public bool IsTypeSwitch => Kind == "type-switch";

    /// <inheritdoc />
    public override string? LanguageTag => "go";

    /// <inheritdoc />
    public override IReadOnlyList<KeyValuePair<string, bool>>? LanguageFlags => [];

    /// <summary>
    /// Registers restoration of Go statements and their shared cached facts.
    /// </summary>
    public static void RegisterCacheFactory() =>
        StatementTypeRegistry.Register("go", (baseDecl, _) => new GoStatementInfo(baseDecl));
}
