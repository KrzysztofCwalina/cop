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
    CopValue Invoke(IReadOnlyList<CopValue> args, Evaluator evaluator, Environment env);
    int Arity { get; }
}
