// Carries captured output and outcome metadata from an external toolchain invocation.

namespace Cop.Core;

/// <summary>
/// An external tool's status, executable path, exit code, output, and diagnostic.
/// </summary>
public sealed record ToolchainResult(
    ToolchainStatus Status,
    string? ExecutablePath,
    int? ExitCode,
    string StandardOutput,
    string StandardError,
    string? Diagnostic);
