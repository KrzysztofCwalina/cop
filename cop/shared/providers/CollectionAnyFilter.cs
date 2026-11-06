// Tests collection elements against a nested provider filter.

using System.Text.Json.Serialization;

namespace Cop.Core;

/// <summary>
/// <c>any</c> — collection item predicate. True if any item in an object collection
/// satisfies the inner filter. E.g., <c>Methods:any(Public)</c>.
/// </summary>
public record CollectionAnyFilter(string Property, FilterExpression ItemFilter) : FilterExpression;
