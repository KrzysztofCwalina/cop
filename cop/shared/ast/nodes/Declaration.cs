// Base syntax node for declarations in a Cop source module.

namespace Cop.Lang.Ast;

/// <summary>
/// Base syntax node for declarations in a Cop source module.
/// </summary>
// ============================================================================
// Declarations (top-level)
// ============================================================================

public abstract record Declaration(int Line = 0) : AstNode(Line);
