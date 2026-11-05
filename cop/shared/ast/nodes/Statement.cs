// Base syntax node for executable statements inside a command block.

namespace Cop.Lang.Ast;

/// <summary>
/// Base syntax node for executable statements inside a command block.
/// </summary>
// ============================================================================
// Statements (inside commands / blocks)
// ============================================================================

public abstract record Statement(int Line = 0) : AstNode(Line);
