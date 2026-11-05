// Represents the named outcome and item count of a Cop assertion.

namespace Cop.Lang;

/// <summary>
/// Result of an ASSERT command.
/// </summary>
public record AssertResult(string Name, bool Passed, string Message, int Count);
