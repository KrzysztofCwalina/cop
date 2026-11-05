// Disjunction — any condition must be true (OR).

using System.Text.Json.Serialization;

namespace Cop.Core;

/// <summary>Disjunction — any condition must be true (OR).</summary>
public record OrFilter(List<FilterExpression> Conditions) : FilterExpression;
