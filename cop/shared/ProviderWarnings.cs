// Nonfatal provider notices, isolated and deduplicated within each engine run.

using System.Collections.Concurrent;

namespace Cop.Core;

/// <summary>Nonfatal provider notices, isolated and deduplicated within each engine run.</summary>
public static class ProviderWarnings
{
    private sealed class State
    {
        public readonly System.Collections.Concurrent.ConcurrentDictionary<(string Provider, string Scope, string Message), byte> Seen = new();
        public readonly System.Collections.Concurrent.ConcurrentQueue<string> Messages = new();
    }

    private static readonly System.Threading.AsyncLocal<State?> s_state = new();
    private static State Current => s_state.Value ??= new();

    /// <summary>
    /// Records a provider warning once per provider, scope, and message.
    /// </summary>
    public static void ReportOnce(string provider, string scope, string message)
    {
        State state = Current;
        if (state.Seen.TryAdd((provider, scope, message), 0))
        {
            state.Messages.Enqueue($"Warning: {provider}: {message}");
        }
    }

    /// <summary>
    /// Removes and returns the warnings recorded in the current execution.
    /// </summary>
    public static IReadOnlyList<string> Drain()
    {
        List<string> result = new List<string>();
        while (Current.Messages.TryDequeue(out string? message))
        {
            result.Add(message);
        }

        return result;
    }

    /// <summary>
    /// Starts a fresh warning state for the current asynchronous execution.
    /// </summary>
    public static void Clear() => s_state.Value = new();
}
