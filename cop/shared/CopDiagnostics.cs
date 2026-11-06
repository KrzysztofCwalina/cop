// Process-wide diagnostic settings, set once from CLI flags in Program.cs and read by the
// engine and by providers.

using System.Collections.Concurrent;

namespace Cop.Core;

/// <summary>
/// Process-wide diagnostic settings, set once from CLI flags in Program.cs and read by the
/// engine and by providers. Providers can read these because the provider load context shares
/// the host assembly (see ProviderLoadContext.Load), so this static is the same instance in
/// both contexts. This replaces the former COP_* diagnostic environment variables — diagnostics
/// are controlled by explicit CLI flags (-d / -dd / -ddd, --ai-log) rather than hidden env vars.
/// </summary>
public static class CopDiagnostics
{
    /// <summary>
    /// Verbosity level: 0 = off, 1 = -d (summaries), 2 = -dd (+ phase/parse timing),
    /// 3 = -ddd (+ per-item evaluator trace firehose).
    /// </summary>
    public static int Level { get; set; }

    /// <summary>Optional path for the AI judge transcript log (--ai-log). Null = disabled.</summary>
    public static string? AiLogPath { get; set; }

    /// <summary>-d and up: emit [diag] summaries.</summary>
    public static bool IsSummaryEnabled => Level >= 1;

    /// <summary>-dd and up: emit provider phase timing and per-file parse timing.</summary>
    public static bool IsTimingEnabled => Level >= 2;

    /// <summary>-ddd and up: emit the per-item evaluator [trace] firehose.</summary>
    public static bool IsTraceEnabled => Level >= 3;
}
