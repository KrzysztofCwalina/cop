// Exposes Python statement categories and preserves them through source caching.

namespace Cop.Providers.SourceModel;

/// <summary>
/// A Python-specific <see cref="StatementInfo"/> exposing Python statement kinds. The Python
/// provider emits this for every Python statement so <c>:asPython</c> narrowing is consistent.
/// </summary>
public sealed class PythonStatementInfo : StatementInfo
{
    /// <summary>Creates a Python statement from parsed syntax and source facts.</summary>
    public PythonStatementInfo(
        string kind, List<string> keywords, string? typeName, string? memberName,
        List<string> arguments, int line, bool isInMethod)
        : base(kind, keywords, typeName, memberName, arguments, line, isInMethod) { }

    /// <summary>Gets whether this is a synchronous context-manager statement.</summary>
    public bool IsWith => Kind == "with";
    /// <summary>Gets whether this is an asynchronous context-manager statement.</summary>
    public bool IsAsyncWith => Kind == "async with";
    /// <summary>Gets whether this is a Python raise statement.</summary>
    public bool IsRaise => Kind == "throw";
    /// <summary>Gets whether this is a Python assertion statement.</summary>
    public bool IsAssert => Kind == "assert";
    /// <summary>Gets whether this statement models a comprehension.</summary>
    public bool IsComprehension => Kind == "comprehension";
    /// <summary>Gets whether this is a global binding declaration.</summary>
    public bool IsGlobal => Kind == "global";
    /// <summary>Gets whether this is a nonlocal binding declaration.</summary>
    public bool IsNonlocal => Kind == "nonlocal";

    /// <inheritdoc />
    public override string? LanguageTag => "python";

    /// <inheritdoc />
    public override IReadOnlyList<KeyValuePair<string, bool>>? LanguageFlags => [];

    /// <summary>
    /// Registers restoration of Python statements from cached source facts.
    /// </summary>
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
            CallTarget = baseDecl.CallTarget,
            ArgumentExpressions = baseDecl.ArgumentExpressions,
            ValueExpression = baseDecl.ValueExpression,
            Column = baseDecl.Column,
            EndLine = baseDecl.EndLine,
            EndColumn = baseDecl.EndColumn,
            _children = baseDecl._children,
            ConstructedTypeInterfaces = baseDecl.ConstructedTypeInterfaces,
        });
}
