// Enumerates set-bit and clear-bit tests supported by provider filters.

using System.Text.Json.Serialization;

namespace Cop.Core;

/// <summary>
/// Flags test operations for <see cref="FlagsFilter"/>.
/// </summary>
public enum FlagsOp
{
    /// <summary><c>isSet</c> — <c>(value &amp; mask) != 0</c></summary>
    IsSet,
    /// <summary><c>isClear</c> — <c>(value &amp; mask) == 0</c></summary>
    IsClear
}
