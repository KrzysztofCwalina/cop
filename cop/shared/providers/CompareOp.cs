// Enumerates numeric comparisons supported by provider filters.

using System.Text.Json.Serialization;

namespace Cop.Core;

/// <summary>
/// Numeric comparison operations for <see cref="ComparisonFilter"/>
/// and <see cref="CollectionCountFilter"/>.
/// </summary>
public enum CompareOp
{
    /// <summary><c>&gt;</c></summary>
    GreaterThan,
    /// <summary><c>&lt;</c></summary>
    LessThan,
    /// <summary><c>==</c></summary>
    Equals,
    /// <summary><c>&gt;=</c></summary>
    GreaterOrEqual,
    /// <summary><c>&lt;=</c></summary>
    LessOrEqual
}
