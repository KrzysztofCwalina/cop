// Binary operators recognized by the Cop parser and evaluator.

namespace Cop.Lang.Ast;

/// <summary>
/// Binary operators recognized by the Cop parser and evaluator.
/// </summary>
// ============================================================================
// Operators
// ============================================================================

public enum BinaryOp
{
    // Arithmetic
    Add,        // +
    Subtract,   // -
    Multiply,   // *
    Divide,     // /
    Modulo,     // %

    // Comparison
    Equal,          // ==
    NotEqual,       // !=
    LessThan,       // <
    GreaterThan,    // >
    LessOrEqual,    // <=
    GreaterOrEqual, // >=

    // Logical
    And,    // &&
    Or,     // ||

    // Bitwise
    BitwiseAnd, // &
    BitwiseOr,  // |
}
