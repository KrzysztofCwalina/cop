// Represents a command function implemented by a statement block.

namespace Cop.Lang.Ast;

/// <summary>
/// Function body is a statement block (only for ALL-UPPERCASE function names):
/// function MAIN() = { foreach items => print(item.Name) }
/// </summary>
public record BlockBody(List<Statement> Statements) : FunctionBody;
