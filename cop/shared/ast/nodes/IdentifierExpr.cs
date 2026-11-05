// Represents a named reference in the executable syntax tree.

namespace Cop.Lang.Ast;

/// <summary>
/// Simple identifier reference: x, myFunc, Type
/// </summary>
public record IdentifierExpr(string Name, int Line = 0) : Expression(Line);
