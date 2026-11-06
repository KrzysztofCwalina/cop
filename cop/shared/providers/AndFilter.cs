// Conjunction — all conditions must be true (AND).

using System.Text.Json.Serialization;

namespace Cop.Core;

/// <summary>Conjunction — all conditions must be true (AND).</summary>
public record AndFilter(List<FilterExpression> Conditions) : FilterExpression;
