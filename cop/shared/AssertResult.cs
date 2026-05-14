namespace Cop.Lang;

/// <summary>
/// Result of an ASSERT command.
/// </summary>
public record AssertResult(string Name, bool Passed, string Message, int Count);
