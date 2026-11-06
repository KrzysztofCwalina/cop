// Owns optional toolchain workers for one execution, including deferred enumeration.

using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Cop.Core;

/// <summary>Owns optional toolchain workers for one execution, including deferred enumeration.</summary>
public sealed class ToolchainExecutionScope : IAsyncDisposable
{
    private static readonly AsyncLocal<ToolchainExecutionScope?> s_ambient = new();
    private readonly ToolchainExecutionScope? _previous;
    private readonly Dictionary<string, ToolchainJsonWorker> _workers = new(StringComparer.Ordinal);
    private bool _disposed;

    public ToolchainExecutionScope()
    {
        _previous = s_ambient.Value;
        s_ambient.Value = this;
    }

    /// <summary>
    /// Reuses a serialized JSON-lines worker in the current engine execution. Outside an execution,
    /// the worker is disposed after this request. A successful response may describe source syntax
    /// errors; interpreting that language-specific payload is the provider's responsibility.
    /// </summary>
    public static async Task<ToolchainResult> RequestAsync(
        ToolchainCommand command,
        string request,
        ToolchainRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        string executable = command.Executable;
        IReadOnlyList<string> arguments = command.Arguments;
        string workingDirectory = command.WorkingDirectory;
        IReadOnlyDictionary<string, string?>? environment = command.Environment;
        TimeSpan? timeout = options?.Timeout;
        int maxOutputCharacters = options?.MaxOutputCharacters ?? 4_194_304;
        ToolchainExecutionScope? scope = s_ambient.Value;
        if (scope is null)
        {
            await using ToolchainJsonWorker worker =
                new ToolchainJsonWorker(executable, arguments, workingDirectory, environment);
            return await worker.RequestAsync(request, timeout, maxOutputCharacters, cancellationToken).ConfigureAwait(false);
        }
        ToolchainJsonWorker shared;
        // JSON preserves argument boundaries, including quotes and separators in paths/scripts.
        string key = JsonSerializer.Serialize(new { executable, arguments, workingDirectory,
            environment = environment?.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray() });
        lock (scope._workers)
        {
            ObjectDisposedException.ThrowIf(scope._disposed, scope);
            if (!scope._workers.TryGetValue(key, out shared!))
            {
                scope._workers.Add(key, shared = new(executable, arguments, workingDirectory, environment));
            }
        }
        return await shared.RequestAsync(request, timeout, maxOutputCharacters, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Disposes owned workers and restores the previous ambient execution scope.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        ToolchainJsonWorker[] workers;
        lock (_workers)
        {
            if (_disposed)
            {
                return ValueTask.CompletedTask;
            }

            _disposed = true;
            workers = _workers.Values.ToArray();
            _workers.Clear();
        }
        // Restore in the caller's context; changes inside async methods do not flow back.
        s_ambient.Value = _previous;
        return DisposeWorkersAsync(workers);
    }

    private static async ValueTask DisposeWorkersAsync(IEnumerable<ToolchainJsonWorker> workers)
    {
        foreach (ToolchainJsonWorker worker in workers)
        {
            await worker.DisposeAsync().ConfigureAwait(false);
        }
    }
}
