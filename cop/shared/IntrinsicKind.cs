// Categorizes primitive operations and utility functions for language tooling.

namespace Cop.Lang;

/// <summary>
/// The tooling category of a built-in operation. One operation name can have
/// separate entries in multiple categories, such as string and numeric equality.
/// </summary>
public enum IntrinsicKind
{
    /// <summary>A boolean test applied to string values.</summary>
    StringPredicate,

    /// <summary>A boolean test applied to numeric values.</summary>
    NumericPredicate,

    /// <summary>A boolean test applied to a collection.</summary>
    CollectionPredicate,

    /// <summary>A boolean test available across value types.</summary>
    UniversalPredicate,

    /// <summary>A boolean test applied to object values.</summary>
    ObjectPredicate,

    /// <summary>An operation that transforms a string value.</summary>
    StringTransform,

    /// <summary>An operation that transforms a collection.</summary>
    CollectionTransform,

    /// <summary>A computed property available on string values.</summary>
    StringProperty,

    /// <summary>A computed property available on collections.</summary>
    CollectionProperty,

    /// <summary>
    /// A utility function rather than a primitive predicate, transform, or property.
    /// Tooling discovers its declarations from the core package; provider pushdown
    /// does not treat the utility name as a property.
    /// </summary>
    Utility,
}
