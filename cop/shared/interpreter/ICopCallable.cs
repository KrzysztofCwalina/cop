// Common interface for anything that can be called.

using Cop.Core;
using Cop.Lang.Ast;

namespace Cop.Lang.Interpreter;

// ============================================================================
// Callable Values
// ============================================================================

/// <summary>
/// Common interface for anything that can be called.
/// </summary>
public interface ICopCallable
{
    /// <summary>
    /// Invokes this callable with evaluated arguments and the current evaluation context.
    /// </summary>
    ValueTask<CopValue> InvokeAsync(IReadOnlyList<CopValue> args, Evaluator evaluator,
        Environment env, CancellationToken cancellationToken = default);

    /// <summary>The number of arguments still required to invoke this callable.</summary>
    int Arity { get; }
}
