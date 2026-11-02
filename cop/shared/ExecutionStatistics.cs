using System.Collections.Concurrent;
using System.Diagnostics;

namespace Cop.Core;

public sealed record ExecutionSummary(int InputFiles, int CommandsExecuted, TimeSpan Elapsed);

/// <summary>Execution-scoped counters shared by the engine and its providers without forcing deferred work.</summary>
public sealed class ExecutionStatistics : IDisposable
{
    private static readonly AsyncLocal<ExecutionStatistics?> Current = new();
    private readonly ExecutionStatistics? _previous;
    private readonly ConcurrentDictionary<string, byte> _files = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private int _commands;

    public ExecutionStatistics()
    {
        _previous = Current.Value;
        Current.Value = this;
    }

    /// <summary>Reports a demanded input file, including cache hits. Repeated reports count once per run.</summary>
    public static void RecordInputFile(string path)
    {
        if (Current.Value is { } current)
            current._files.TryAdd(Path.GetFullPath(path), 0);
    }

    public void CommandCompleted() => Interlocked.Increment(ref _commands);

    public ExecutionSummary Snapshot() => new(_files.Count, Volatile.Read(ref _commands), _elapsed.Elapsed);

    public void Dispose() => Current.Value = _previous;
}
