namespace Cop.Lang;

/// <summary>
/// Result of an ASSERT or ASSERT_EMPTY command.
/// </summary>
public record AssertResult(string Name, bool Passed, string Message, int Count);
