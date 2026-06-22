namespace Cop.Providers.SourceModel;

/// <summary>
/// A C#-specific <see cref="StatementInfo"/> carrying language-specific control-flow and
/// error-handling facts that have no place in the language-agnostic common model — e.g.
/// lock / unsafe / fixed / checked blocks, yield, goto, await foreach, and exception filters
/// (catch ... when).
///
/// The C# provider emits this for EVERY C# statement (just as every C# type is a
/// CSharpTypeDeclaration), so a :asCSharp narrowing always yields CSharpStatement-typed items.
/// Most flags derive from Kind; the two that need extra syntax context are set at construction.
/// </summary>
public sealed class CSharpStatementInfo : StatementInfo
{
    public CSharpStatementInfo(
        string kind, List<string> keywords, string? typeName, string? memberName,
        List<string> arguments, int line, bool isInMethod)
        : base(kind, keywords, typeName, memberName, arguments, line, isInMethod) { }

    /// <summary>True for a C# lock statement.</summary>
    public bool IsLock => Kind == "lock";

    /// <summary>True for a C# unsafe block statement.</summary>
    public bool IsUnsafe => Kind == "unsafe";

    /// <summary>True for a C# fixed statement.</summary>
    public bool IsFixed => Kind == "fixed";

    /// <summary>True for a C# checked block statement.</summary>
    public bool IsChecked => Kind == "checked";

    /// <summary>True for a C# unchecked block statement.</summary>
    public bool IsUnchecked => Kind == "unchecked";

    /// <summary>True for a C# yield return / yield break statement.</summary>
    public bool IsYield => Kind == "yield";

    /// <summary>True for a C# goto statement.</summary>
    public bool IsGoto => Kind == "goto";

    /// <summary>True for a C# await foreach (async stream) statement.</summary>
    public bool IsAwaitForeach { get; init; }

    /// <summary>True for a catch clause with an exception filter (when (...)).</summary>
    public bool HasCatchFilter { get; init; }
}
