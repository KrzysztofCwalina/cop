// Type reference (used in annotations).

namespace Cop.Lang.Ast;

/// <summary>
/// Type reference (used in annotations).
/// Constraint is used for type parameter bounds: [T:comparable] → Constraint="comparable"
/// </summary>
public record TypeRef(string Name, bool IsCollection = false, int Line = 0, string? Constraint = null) : AstNode(Line);
