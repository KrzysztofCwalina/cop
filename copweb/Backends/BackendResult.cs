namespace Cop.Driver.Backends;

/// <summary>
/// Result of a backend execution step.
/// </summary>
public record BackendResult(bool Success, string Message, string? Details = null);
