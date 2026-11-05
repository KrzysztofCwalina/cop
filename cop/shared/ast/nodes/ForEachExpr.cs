// Embeds a side-effecting loop in an expression and evaluates to the null value.

namespace Cop.Lang.Ast;

/// <summary>
/// A foreach used in expression position (e.g. as an operand of `&`). Runs the wrapped loop for
/// its side effects (auto-printed body results) and evaluates to nil. Lets `print(a) & foreach
/// xs => print(item)` chain a loop after other output-producing expressions.
/// </summary>
public record ForEachExpr(ForEachStatement Loop, int Line = 0) : Expression(Line);
