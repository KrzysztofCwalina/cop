// Identifies a toolchain process without mixing launch settings with request limits.

namespace Cop.Core;

/// <summary>Immutable launch settings shared by one-shot and persistent toolchains.</summary>
/// <param name="Executable">An installed executable name or absolute discovered path.</param>
/// <param name="Arguments">Arguments passed individually without a command shell.</param>
/// <param name="WorkingDirectory">The absolute working directory used for the process.</param>
/// <param name="Environment">Optional environment overrides for this process only.</param>
public sealed record ToolchainCommand(
    string Executable,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string?>? Environment = null);
