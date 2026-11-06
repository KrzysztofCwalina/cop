// A named reference in the public query source model.

namespace Cop.Lang;

/// <summary>
/// A named reference in the public query source model.
/// </summary>
public record IdentifierExpr(string Name) : Expression;
