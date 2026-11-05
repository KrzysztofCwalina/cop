// Owns optional toolchain workers for one execution, including deferred enumeration.

using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Cop.Core;

/// <summary>Owns optional toolchain workers for one execution, including deferred enumeration.</summary>
public sealed class ToolchainExecutionScope : IDisposable
{
    private static readonly AsyncLocal<ToolchainExecutionScope?> Ambient = new();
    private readonly ToolchainExecutionScope? _previous;
    private readonly Dictionary<string, ToolchainJsonWorker> _workers = new(StringComparer.Ordinal);
    private bool _disposed;

    public ToolchainExecutionScope()
    {
        _previous = Ambient.Value;
        Ambient.Value = this;
    }

    /// <summary>
    /// Reuses a serialized JSON-lines worker in the current engine execution. Outside an execution,
    /// the worker is disposed after this request. A successful response may describe source syntax
    /// errors; interpreting that language-specific payload is the provider's responsibility.
    /// </summary>
    public static async Task<ToolchainResult> RequestAsync(
        string executable, IReadOnlyList<string> arguments, string workingDirectory, string request,
        TimeSpan? timeout = null, int maxOutputCharacters = 4_194_304,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        ToolchainExecutionScope? scope = Ambient.Value;
        if (scope is null)
        {
            using ToolchainJsonWorker worker = new ToolchainJsonWorker(executable, arguments, workingDirectory, environment);
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

    public void Dispose()
    {
        lock (_workers)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (ToolchainJsonWorker worker in _workers.Values)
            {
                worker.Dispose();
            }

            _workers.Clear();
        }
        Ambient.Value = _previous;
    }
}
