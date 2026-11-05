// Collects provider failures so the engine can report errors detected during data loading.

using System.Collections.Concurrent;

namespace Cop.Core;

/// <summary>
/// A process-wide sink for errors detected INSIDE a provider (e.g. a source file that fails to parse).
/// Providers run in a shared assembly context with the host, so this static is the same instance in
/// both — it lets a provider surface an error to the engine, which drains it after querying and
/// reports it. Without this, provider-side failures were swallowed (printed to stderr at best) and a
/// run "succeeded" (exit 0) despite an incomplete or wrong model.
/// </summary>
public static class ProviderErrors
{
    // Per async-context (per Engine.Run) rather than a single process-wide queue. The engine does
    // Clear() at the start of a run and Drain() at the end; with a shared static queue, two
    // concurrent runs (parallel tests, or the LSP analyzing in the background while the CLI runs)
    // corrupted each other's error lists — one run's Clear() discarded the other's reports. An
    // AsyncLocal isolates each run's queue. Engine.Run calls Clear() first, which establishes the
    // queue in the run's context so the providers it then invokes report into that same queue.
    private static readonly System.Threading.AsyncLocal<System.Collections.Concurrent.ConcurrentQueue<string>?> _errors = new();

    private static System.Collections.Concurrent.ConcurrentQueue<string> Queue => _errors.Value ??= new();

    /// <summary>Reports a provider-side error so the engine can surface it.</summary>
    public static void Report(string message) => Queue.Enqueue(message);

    /// <summary>Removes and returns all reported provider errors (deduplicated, order-preserving).</summary>
    public static IReadOnlyList<string> Drain()
    {
        ConcurrentQueue<string> queue = Queue;
        List<string> result = new List<string>();
        HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
        while (queue.TryDequeue(out string? e))
        {
            if (seen.Add(e))
            {
                result.Add(e);
            }
        }

        return result;
    }

    /// <summary>Resets the current run's provider errors (called at the start of each run).</summary>
    public static void Clear() => _errors.Value = new System.Collections.Concurrent.ConcurrentQueue<string>();
}
