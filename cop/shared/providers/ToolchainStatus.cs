// Distinguishes successful tool execution from discovery and resource-limit failures.

namespace Cop.Core;

/// <summary>Outcome of invoking an optional external toolchain.</summary>
public enum ToolchainStatus
{
    /// <summary>The tool completed successfully.</summary>
    Success,

    /// <summary>No usable executable was available for invocation.</summary>
    Unavailable,

    /// <summary>The tool failed to start, execute, or complete successfully.</summary>
    Failed,

    /// <summary>The invocation exceeded its allowed execution time.</summary>
    TimedOut,

    /// <summary>The tool produced more output than the configured limit.</summary>
    OutputLimitExceeded
}
