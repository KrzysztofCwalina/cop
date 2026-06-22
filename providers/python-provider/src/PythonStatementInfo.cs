namespace Cop.Providers.SourceModel;

/// <summary>
/// A Python-specific <see cref="StatementInfo"/> exposing Python statement kinds. The Python
/// provider emits this for every Python statement so <c>:asPython</c> narrowing is consistent.
/// </summary>
public sealed class PythonStatementInfo : StatementInfo
{
    public PythonStatementInfo(
        string kind, List<string> keywords, string? typeName, string? memberName,
        List<string> arguments, int line, bool isInMethod)
        : base(kind, keywords, typeName, memberName, arguments, line, isInMethod) { }

    public bool IsWith => Kind == "with";

    public bool IsAsyncWith => Kind == "async with";

    public bool IsRaise => Kind == "throw";

    public bool IsAssert => Kind == "assert";

    public bool IsComprehension => Kind == "comprehension";

    public bool IsGlobal => Kind == "global";

    public bool IsNonlocal => Kind == "nonlocal";

    public override string? LanguageTag => "python";

    public override IReadOnlyList<KeyValuePair<string, bool>>? LanguageFlags => [];

    public static void RegisterCacheFactory() =>
        StatementTypeRegistry.Register("python", (baseDecl, _) => new PythonStatementInfo(
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
            Method = baseDecl.Method,
            Parent = baseDecl.Parent,
            CopIgnore = baseDecl.CopIgnore,
            Condition = baseDecl.Condition,
            Expression = baseDecl.Expression,
            _children = baseDecl._children,
            ConstructedTypeInterfaces = baseDecl.ConstructedTypeInterfaces,
        });
}
