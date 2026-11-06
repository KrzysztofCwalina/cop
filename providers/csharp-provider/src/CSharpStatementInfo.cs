// Adds C# control-flow and resource-initializer facts to the shared statement model.

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

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
    internal int SyntaxStart { get; set; } = -1;
    internal bool RequiresTypeNameAnalysis { get; init; }
    internal Lazy<bool>? DeferredCatchFilterAnalysis { get; set; }
    internal Lazy<bool>? DeferredOwnershipAnalysis { get; set; }
    internal Lazy<(bool IsHandler, bool PreservesCaller)>? DeferredCancellationAnalysis
    {
        get; set;
    }

    /// <summary>
    /// Captures a C# statement and its precise syntax span and resource facts.
    /// </summary>
    public CSharpStatementInfo(StatementInfo source, SyntaxNode? syntax = null)
        : base(source)
    {
        if (syntax is null)
        {
            return;
        }

        FileLinePositionSpan span = syntax.GetLocation().GetLineSpan();
        Column = span.StartLinePosition.Character + 1;
        EndLine = span.EndLinePosition.Line + 1;
        EndColumn = span.EndLinePosition.Character + 1;
        SyntaxStart = syntax.SpanStart;
        IsUsingResource = Kind == "call" && IsUsingResourceExpression(syntax);
    }

    private static bool IsUsingResourceExpression(SyntaxNode syntax)
    {
        while (syntax.Parent is ParenthesizedExpressionSyntax parenthesized)
        {
            syntax = parenthesized;
        }

        if (syntax.Parent is UsingStatementSyntax statement)
        {
            return ReferenceEquals(statement.Expression, syntax);
        }

        if (syntax.Parent is not EqualsValueClauseSyntax initializer
            || initializer.Parent is not VariableDeclaratorSyntax variable
            || variable.Parent is not VariableDeclarationSyntax declaration)
        {
            return false;
        }

        return declaration.Parent is UsingStatementSyntax
            || declaration.Parent is LocalDeclarationStatementSyntax { UsingKeyword.RawKind: not 0 };
    }

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

    /// <summary>
    /// True when every supported explicit catch-body path rethrows the original exception.
    /// Unsupported control flow is false. This is not an exception-effects proof:
    /// implicit exceptions from calls, disposal, monitors, and cleanup are not modeled.
    /// </summary>
    public bool IsOriginalExceptionAlwaysRethrown { get; init; }

    /// <summary>True when a bound filter admits only specific exception subtypes.</summary>
    public bool HasTypeRestrictedCatchFilter => DeferredCatchFilterAnalysis?.Value == true;

    /// <summary>True for a bound OperationCanceledException or derived typed catch.</summary>
    public bool IsCancellationHandler => DeferredCancellationAnalysis?.Value.IsHandler == true;

    /// <summary>
    /// True for supported explicit rethrows, operation-linked caller-token guards/filters,
    /// consumed fresh cancellation completions, or exclusively internal-token awaits.
    /// Nested cleanup handlers must preserve cancellation, not rethrow an arbitrary error.
    /// Unknown flow is false; implicit exception effects and cleanup are not modeled.
    /// </summary>
    public bool IsCallerCancellationPreserved =>
        DeferredCancellationAnalysis?.Value.PreservesCaller == true;

    /// <summary>
    /// True for a direct return/member assignment or a local transferred on every supported
    /// explicit path. This proves an escape site, not that the receiver disposes the value.
    /// Implicit exceptions from calls and cleanup are outside this flow proof.
    /// </summary>
    public bool IsOwnershipTransferred => DeferredOwnershipAnalysis?.Value == true;

    /// <summary>True when this call directly initializes a using or await using resource.</summary>
    public bool IsUsingResource { get; init; }
}
