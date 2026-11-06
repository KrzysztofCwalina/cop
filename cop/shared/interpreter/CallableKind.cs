// Distinguishes different callable flavors for downstream phases.

namespace Cop.Lang.Interpreter;

using Cop.Lang.Ast;

/// <summary>
/// Distinguishes different callable flavors for downstream phases.
/// </summary>
public enum CallableKind
{
    Function,
    Predicate,
    Command,
    External
}
