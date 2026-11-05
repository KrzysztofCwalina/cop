// Numeric comparison on a property: e.g., Size > 1000, Line == 42.

using System.Text.Json.Serialization;

namespace Cop.Core;

/// <summary>
/// Numeric comparison on a property: e.g., <c>Size > 1000</c>, <c>Line == 42</c>.
/// Supports <c>&gt;</c>, <c>&lt;</c>, <c>&gt;=</c>, <c>&lt;=</c>, and <c>==</c>.
/// </summary>
public record ComparisonFilter(string Property, CompareOp Op, double Value) : FilterExpression;
