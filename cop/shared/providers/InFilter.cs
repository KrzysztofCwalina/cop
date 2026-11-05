// Tests whether a property equals one of the listed string values.

using System.Text.Json.Serialization;

namespace Cop.Core;

/// <summary>
/// <c>in</c> — "in list". True if the property value equals any value in the list
/// (case-insensitive). E.g., <c>Extension:in(['.cs', '.vb'])</c>.
/// </summary>
public record InFilter(string Property, List<string> Values) : FilterExpression;
