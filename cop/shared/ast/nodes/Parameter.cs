// Function/lambda parameter.

namespace Cop.Lang.Ast;

// ============================================================================
// Supporting Types
// ============================================================================

/// <summary>
/// Function/lambda parameter.
/// </summary>
public record Parameter(string Name, TypeRef? Type = null, int Line = 0) : AstNode(Line);
