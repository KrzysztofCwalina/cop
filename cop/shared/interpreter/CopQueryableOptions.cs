// Describes the filter and element shape used to initialize a deferred provider query.

using Cop.Core;

namespace Cop.Lang.Interpreter;

/// <summary>Optional filter and element metadata for a deferred provider collection.</summary>
public sealed record CopQueryableOptions
{
    /// <summary>A filter overriding the initial filter already present in the request.</summary>
    public FilterExpression? Filter { get; init; }

    /// <summary>The optional Cop type name of the collection's elements.</summary>
    public string? ItemType { get; init; }

    /// <summary>The scalar member types eligible for provider-side predicate pushdown.</summary>
    public IReadOnlyDictionary<string, string>? ScalarProperties { get; init; }
}
