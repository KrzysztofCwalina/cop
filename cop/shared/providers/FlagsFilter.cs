// Tests whether bits are set or clear in an integer-valued property.

using System.Text.Json.Serialization;

namespace Cop.Core;

/// <summary>
/// Flags (bitwise) test on an integer property.
/// <c>isSet</c>: <c>(value &amp; mask) != 0</c> — at least one flag in mask is set.
/// <c>isClear</c>: <c>(value &amp; mask) == 0</c> — none of the flags in mask are set.
/// E.g., <c>Modifiers:isSet(Public)</c>, <c>Modifiers:isClear(Static)</c>.
/// </summary>
public record FlagsFilter(string Property, FlagsOp Op, long Value) : FilterExpression;
