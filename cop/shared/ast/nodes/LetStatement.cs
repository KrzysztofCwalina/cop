// Local let binding within a block.

namespace Cop.Lang.Ast;

/// <summary>
/// Local let binding within a block.
/// </summary>
public record LetStatement(string Name, TypeRef? TypeAnnotation, Expression Value, int Line = 0) : Statement(Line);
