// Compares the size of a collection-valued property with a constant.

using System.Text.Json.Serialization;

namespace Cop.Core;

/// <summary>
/// Collection count comparison. E.g., <c>Parameters.Count > 3</c>
/// or an equivalent predicate on the collection length.
/// </summary>
public record CollectionCountFilter(string Property, CompareOp Op, int Value) : FilterExpression;
