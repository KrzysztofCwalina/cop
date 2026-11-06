// Groups target selection, command arguments, and reporting options for CLI runs.

using Cop.Core;

namespace Cop.Cli.Commands;

/// <summary>Configures a local program, remote script, or package execution.</summary>
public sealed record RunOptions
{
    /// <summary>Arguments forwarded to the selected Cop command.</summary>
    public IReadOnlyList<string>? Arguments { get; init; }

    /// <summary>Analysis directory, defaulting to the current directory.</summary>
    public string? Target { get; init; }

    /// <summary>Requested output format, such as text or JSON.</summary>
    public string? OutputFormat { get; init; }

    /// <summary>Explicit command names to execute instead of the default MAIN command.</summary>
    public IReadOnlyList<string>? CommandNames { get; init; }

    /// <summary>Whether to emit execution diagnostics.</summary>
    public bool IsDiagnosticEnabled { get; init; }

    /// <summary>Whether analysis should be skipped when Git reports no modified files.</summary>
    public bool ShouldSkipUnmodifiedTargets { get; init; }

    /// <summary>Additional provider packages requested by the caller.</summary>
    public IReadOnlyList<string>? ProviderNames { get; init; }

    /// <summary>Whether to collect per-rule execution timings.</summary>
    public bool IsRuleProfilingEnabled { get; init; }

    /// <summary>Changed-code review settings for the analyzed target.</summary>
    public ReviewOptions? ReviewOptions { get; init; }
}
