// Tests the boolean value of a named provider property.

using System.Text.Json.Serialization;

namespace Cop.Core;

/// <summary>Boolean property check: e.g., <c>Public</c> (true) or <c>!Abstract</c> (false).</summary>
public record PropertyFilter(string Property, bool Value) : FilterExpression;
