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

    private static readonly System.Threading.AsyncLocal<State?> _state = new();
    private static State Current => _state.Value ??= new();

    public static void ReportOnce(string provider, string scope, string message)
    {
        State state = Current;
        if (state.Seen.TryAdd((provider, scope, message), 0))
        {
            state.Messages.Enqueue($"Warning: {provider}: {message}");
        }
    }

    public static IReadOnlyList<string> Drain()
    {
        List<string> result = new List<string>();
        while (Current.Messages.TryDequeue(out string? message))
        {
            result.Add(message);
        }

        return result;
    }

    public static void Clear() => _state.Value = new();
}
