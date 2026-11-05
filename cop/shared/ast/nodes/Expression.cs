// Base syntax node for expressions that produce runtime values.

namespace Cop.Lang.Ast;

/// <summary>
/// Base syntax node for expressions that produce runtime values.
/// </summary>
// ============================================================================
// Expressions
// ============================================================================

public abstract record Expression(int Line = 0) : AstNode(Line);
