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
    public static int Level;

    /// <summary>Optional path for the AI judge transcript log (--ai-log). Null = disabled.</summary>
    public static string? AiLogPath;

    /// <summary>-d and up: emit [diag] summaries.</summary>
    public static bool Summaries => Level >= 1;

    /// <summary>-dd and up: emit provider phase timing and per-file parse timing.</summary>
    public static bool Timing => Level >= 2;

    /// <summary>-ddd and up: emit the per-item evaluator [trace] firehose.</summary>
    public static bool Trace => Level >= 3;
}

/// <summary>
/// A process-wide sink for errors detected INSIDE a provider (e.g. a source file that fails to parse).
/// Providers run in a shared assembly context with the host, so this static is the same instance in
/// both — it lets a provider surface an error to the engine, which drains it after querying and
/// reports it. Without this, provider-side failures were swallowed (printed to stderr at best) and a
/// run "succeeded" (exit 0) despite an incomplete or wrong model.
/// </summary>
public static class ProviderErrors
{
    private static readonly System.Collections.Concurrent.ConcurrentQueue<string> _errors = new();

    /// <summary>Reports a provider-side error so the engine can surface it.</summary>
    public static void Report(string message) => _errors.Enqueue(message);

    /// <summary>Removes and returns all reported provider errors (deduplicated, order-preserving).</summary>
    public static IReadOnlyList<string> Drain()
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (_errors.TryDequeue(out var e))
            if (seen.Add(e))
                result.Add(e);
        return result;
    }

    /// <summary>Discards any pending provider errors (used to reset state between runs/tests).</summary>
    public static void Clear()
    {
        while (_errors.TryDequeue(out _)) { }
    }
}
