// Base class for all AST nodes.

namespace Cop.Lang.Ast;

// ============================================================================
// AST Root
// ============================================================================

/// <summary>
/// Base class for all AST nodes. Every node carries a source location for diagnostics.
/// </summary>
public abstract record AstNode(int Line = 0);
