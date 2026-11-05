// Field initializer in an object literal.

namespace Cop.Lang.Ast;

/// <summary>
/// Field initializer in an object literal.
/// </summary>
public record FieldInit(string Name, Expression Value, int Line = 0) : AstNode(Line);
