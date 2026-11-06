// Configures request input and resource limits independently of process launch identity.

namespace Cop.Core;

/// <summary>Input and bounded execution settings for an external toolchain request.</summary>
public sealed record ToolchainRunOptions
{
    /// <summary>The optional complete input supplied to a one-shot toolchain process.</summary>
    public string? StandardInput { get; init; }

    /// <summary>The request deadline, or null to use the standard thirty-second limit.</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>The combined output character limit applied to captured process streams.</summary>
    public int MaxOutputCharacters { get; init; } = 4_194_304;
}
