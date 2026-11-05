// Tests string membership in a collection-valued property.

using System.Text.Json.Serialization;

namespace Cop.Core;

/// <summary>
/// <c>contains</c> — collection membership. True if a collection property (list of strings)
/// contains the given value. E.g., <c>Keywords:contains('var')</c>.
/// </summary>
public record CollectionContainsFilter(string Property, string Value) : FilterExpression;
