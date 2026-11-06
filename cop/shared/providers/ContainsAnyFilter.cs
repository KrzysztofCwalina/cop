// Tests whether a string property contains any listed substring.

using System.Text.Json.Serialization;

namespace Cop.Core;

/// <summary>
/// <c>ca</c> — "contains any". True if the property string contains any value in the list
/// as a substring (case-insensitive). E.g., <c>Name:ca(['Get', 'Set'])</c>.
/// </summary>
public record ContainsAnyFilter(string Property, List<string> Values) : FilterExpression;
