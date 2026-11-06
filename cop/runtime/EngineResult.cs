// Describes program outputs, diagnostics, exit status, and execution statistics.

using Cop.Core;
using Cop.Lang;

namespace Cop.Providers;

/// <summary>
/// Outputs and diagnostics produced by running the Cop engine.
/// </summary>
public record EngineResult(
    List<PrintOutput> Outputs,
    List<string> ParseErrors,
    List<string> Errors,
    string? CommandName = null,
    List<FileOutput>? FileOutputs = null,
    List<string>? Warnings = null,
    List<AssertResult>? Asserts = null,
    List<CopDiagnostic>? Diagnostics = null,
    int? ExitCode = null)
{
    /// <summary>Whether one or more source files failed to parse.</summary>
    public bool HasParseErrors => ParseErrors.Count > 0;

    /// <summary>Whether loading or execution failed.</summary>
    public bool HasFatalErrors => Errors.Count > 0;

    /// <summary>Whether execution selected a named command.</summary>
    public bool IsCommandMode => CommandName != null;

    /// <summary>Counts and timings collected during execution.</summary>
    public ExecutionSummary? Statistics { get; init; }
}
