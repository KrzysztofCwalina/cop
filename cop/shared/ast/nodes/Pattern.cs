// Base syntax node for patterns used by match-expression arms.

namespace Cop.Lang.Ast;

/// <summary>
/// Base syntax node for patterns used by match-expression arms.
/// </summary>
// ============================================================================
// Patterns (for match expressions)
// ============================================================================

public abstract record Pattern(int Line = 0) : AstNode(Line);
