using Cop.Providers;

namespace Cop.Providers.SourceModel;

/// <summary>
/// A Go-specific <see cref="StatementInfo"/>. Its current Go-only facts are derived from
/// parser-emitted statement kinds, so no extra flags are needed for cache round-tripping.
/// </summary>
public sealed class GoStatementInfo : StatementInfo
{
    public GoStatementInfo(
        string kind, List<string> keywords, string? typeName, string? memberName,
        List<string> arguments, int line, bool isInMethod)
        : base(kind, keywords, typeName, memberName, arguments, line, isInMethod) { }

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

    public override string? LanguageTag => "go";

    public override IReadOnlyList<KeyValuePair<string, bool>>? LanguageFlags => [];

    public static void RegisterCacheFactory() =>
        StatementTypeRegistry.Register("go", (baseDecl, _) => new GoStatementInfo(
            baseDecl.Kind,
            baseDecl.Keywords,
            baseDecl.TypeName,
            baseDecl.MemberName,
            baseDecl.Arguments,
            baseDecl.Line,
            baseDecl.IsInMethod)
        {
            HasRethrow = baseDecl.HasRethrow,
            IsErrorHandler = baseDecl.IsErrorHandler,
            IsGenericErrorHandler = baseDecl.IsGenericErrorHandler,
            IsBraced = baseDecl.IsBraced,
            CopIgnore = baseDecl.CopIgnore,
            Method = baseDecl.Method,
            Parent = baseDecl.Parent,
            Condition = baseDecl.Condition,
            Expression = baseDecl.Expression,
            ConstructedTypeInterfaces = baseDecl.ConstructedTypeInterfaces,
            File = baseDecl.File,
            _children = baseDecl._children
        });
}
