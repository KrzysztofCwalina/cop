// Negation of an inner expression (NOT).

using System.Text.Json.Serialization;

namespace Cop.Core;

/// <summary>Negation of an inner expression (NOT).</summary>
public record NotFilter(FilterExpression Inner) : FilterExpression;
