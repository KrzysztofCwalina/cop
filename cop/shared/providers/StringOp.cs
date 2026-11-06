// Enumerates the string comparisons supported by provider filters.

using System.Text.Json.Serialization;

namespace Cop.Core;

/// <summary>
/// String comparison operations for <see cref="StringOpFilter"/>.
/// All operations are case-insensitive except <see cref="Matches"/> (regex)
/// and <see cref="Same"/> (case-sensitive exact equality).
/// </summary>
public enum StringOp
{
    /// <summary><c>sw</c> — case-insensitive starts-with.</summary>
    StartsWith,
    /// <summary><c>ew</c> — case-insensitive ends-with.</summary>
    EndsWith,
    /// <summary><c>ct</c> — case-insensitive substring (contains).</summary>
    Contains,
    /// <summary><c>eq</c> — case-insensitive equality.</summary>
    Equals,
    /// <summary><c>rx</c> — case-sensitive regular expression match.</summary>
    Matches,
    /// <summary>Case-sensitive exact equality, distinct from the core-package sameAs predicate.</summary>
    Same,
    /// <summary>Ordinal equality used by the language's == and != operators.</summary>
    ExactEquals
}
