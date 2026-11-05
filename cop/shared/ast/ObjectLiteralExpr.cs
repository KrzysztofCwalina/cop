// A source-model object initializer with named fields and an optional type.

namespace Cop.Lang;

/// <summary>
/// A source-model object initializer with named fields and an optional type.
/// </summary>
public record ObjectLiteralExpr(string? TypeName, Dictionary<string, Expression> Fields) : Expression;
