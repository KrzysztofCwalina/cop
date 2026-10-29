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
        var scope = Ambient.Value;
        if (scope is null)
        {
            using var worker = new ToolchainJsonWorker(executable, arguments, workingDirectory, environment);
            return await worker.RequestAsync(request, timeout, maxOutputCharacters, cancellationToken).ConfigureAwait(false);
        }
        ToolchainJsonWorker shared;
        // JSON preserves argument boundaries, including quotes and separators in paths/scripts.
        var key = JsonSerializer.Serialize(new { executable, arguments, workingDirectory,
            environment = environment?.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray() });
        lock (scope._workers)
        {
            ObjectDisposedException.ThrowIf(scope._disposed, scope);
            if (!scope._workers.TryGetValue(key, out shared!))
                scope._workers.Add(key, shared = new(executable, arguments, workingDirectory, environment));
        }
        return await shared.RequestAsync(request, timeout, maxOutputCharacters, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        lock (_workers)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var worker in _workers.Values) worker.Dispose();
            _workers.Clear();
        }
        Ambient.Value = _previous;
    }
}

/// <summary>A bounded, serialized one-request/one-response JSON-lines process, without a shell.</summary>
public sealed class ToolchainJsonWorker : IDisposable
{
    private readonly string _executable;
    private readonly string[] _arguments;
    private readonly string _workingDirectory;
    private readonly Dictionary<string, string?>? _environment;
    private readonly SemaphoreSlim _requests = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly StringBuilder _stderr = new();
    private Process? _process;
    private ToolchainProcessContainment? _containment;
    private Task? _stderrTask;
    private ToolchainResult? _failure;
    private bool _disposed;
    private volatile bool _stderrOverflow;
    private string? _path;

    public ToolchainJsonWorker(string executable, IReadOnlyList<string> arguments, string workingDirectory,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(arguments);
        _executable = executable;
        _arguments = arguments.ToArray();
        _workingDirectory = Path.GetFullPath(workingDirectory);
        _environment = environment?.ToDictionary(pair => pair.Key, pair => pair.Value);
    }

    public async Task<ToolchainResult> RequestAsync(string request, TimeSpan? timeout = null,
        int maxOutputCharacters = 4_194_304, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Contains('\n') || request.Contains('\r'))
            throw new ArgumentException("A worker request must be a single JSON line.", nameof(request));
        using (JsonDocument.Parse(request)) { }
        var duration = timeout ?? TimeSpan.FromSeconds(30);
        if (duration <= TimeSpan.Zero || duration.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        if (maxOutputCharacters <= 0) throw new ArgumentOutOfRangeException(nameof(maxOutputCharacters));
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_failure is not null) return _failure;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        deadline.CancelAfter(duration);
        bool entered = false;
        try
        {
            await _requests.WaitAsync(deadline.Token).ConfigureAwait(false);
            entered = true;
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_failure is not null) return _failure;
            if (_process is null)
            {
                var startFailure = Start();
                if (startFailure is not null) return _failure = startFailure;
            }
            await _process!.StandardInput.WriteLineAsync(request.AsMemory(), deadline.Token).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync(deadline.Token).ConfigureAwait(false);
            var response = await ReadResponseAsync(maxOutputCharacters, deadline.Token).ConfigureAwait(false);
            using (JsonDocument.Parse(response)) { }
            if (_stderrOverflow)
                return Fail(ToolchainStatus.OutputLimitExceeded, "Worker stderr exceeded its 65536 character limit.");
            return new(ToolchainStatus.Success, _path, null, response, Stderr(), null);
        }
        catch (OperationCanceledException)
        {
            // A timed-out queued request must not kill another caller's active request.
            if (entered) Stop();
            cancellationToken.ThrowIfCancellationRequested();
            return entered
                ? _failure = Result(ToolchainStatus.TimedOut, $"Worker exceeded its {duration.TotalSeconds:g} second deadline.")
                : Result(ToolchainStatus.TimedOut, $"Waiting for worker exceeded its {duration.TotalSeconds:g} second deadline.");
        }
        catch (OutputLimitException)
        {
            return Fail(ToolchainStatus.OutputLimitExceeded, $"Worker response exceeded its {maxOutputCharacters} character limit.");
        }
        catch (Exception ex) when (ex is IOException or JsonException or Win32Exception)
        {
            return Fail(ToolchainStatus.Failed, $"Worker request failed: {ex.Message}");
        }
        finally
        {
            if (entered) _requests.Release();
        }
    }

    private ToolchainResult? Start()
    {
        if (!Directory.Exists(_workingDirectory))
            throw new DirectoryNotFoundException($"Toolchain working directory does not exist: {_workingDirectory}");
        _path = ToolchainProcess.FindExecutable(_executable);
        if (_path is null)
            return Result(ToolchainStatus.Unavailable, $"Toolchain '{_executable}' is not installed on PATH.");
        _process = new Process
        {
            StartInfo = new(_path)
            {
                WorkingDirectory = _workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            }
        };
        foreach (var argument in _arguments) _process.StartInfo.ArgumentList.Add(argument);
        if (_environment is not null)
            foreach (var (name, value) in _environment) _process.StartInfo.Environment[name] = value;
        try
        {
            _containment = new();
            if (!_process.Start()) return Result(ToolchainStatus.Failed, $"Could not start worker '{_path}'.");
            _containment.Attach(_process);
        }
        catch (Win32Exception ex)
        {
            return Result(ex.NativeErrorCode is 2 or 3 ? ToolchainStatus.Unavailable : ToolchainStatus.Failed,
                $"Cannot start worker '{_path}': {ex.Message}");
        }
        _stderrTask = CaptureStderrAsync();
        return null;
    }

    private async Task CaptureStderrAsync()
    {
        var buffer = new char[4096];
        try
        {
            while (true)
            {
                var count = await _process!.StandardError.ReadAsync(buffer.AsMemory(), _lifetime.Token).ConfigureAwait(false);
                if (count == 0) return;
                lock (_stderr)
                {
                    int accepted = Math.Min(count, 65536 - _stderr.Length);
                    _stderr.Append(buffer, 0, accepted);
                    if (accepted < count) _stderrOverflow = true;
                }
                if (_stderrOverflow)
                {
                    _containment?.Terminate();
                    if (!_process.HasExited) _process.Kill(entireProcessTree: true);
                    return;
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException)
        {
            if (!_lifetime.IsCancellationRequested)
                lock (_stderr) _stderr.Append($" [stderr read failed: {ex.Message}]");
        }
    }

    private async Task<string> ReadResponseAsync(int limit, CancellationToken cancellationToken)
    {
        var response = new StringBuilder();
        var buffer = new char[4096];
        while (true)
        {
            var count = await _process!.StandardOutput.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (count == 0)
                throw new IOException($"Worker closed stdout (exit {(_process.HasExited ? _process.ExitCode : null)}). {Stderr()}");
            int newline = Array.IndexOf(buffer, '\n', 0, count);
            int content = newline < 0 ? count : newline;
            if (response.Length + content > limit) throw new OutputLimitException();
            response.Append(buffer, 0, content);
            if (newline >= 0)
            {
                if (newline != count - 1)
                    throw new IOException("Worker emitted unsolicited data after its JSON response.");
                return response.ToString().TrimEnd('\r');
            }
        }
    }

    private string Stderr() { lock (_stderr) return _stderr.ToString(); }

    private ToolchainResult Result(ToolchainStatus status, string diagnostic) =>
        new(status, _path, _process is { } p && HasExited(p) ? p.ExitCode : null, "", Stderr(), diagnostic);

    private static bool HasExited(Process process)
    {
        try { return process.HasExited; }
        catch (InvalidOperationException) { return false; }
    }

    private ToolchainResult Fail(ToolchainStatus status, string diagnostic)
    {
        Stop();
        if (_stderrOverflow) status = ToolchainStatus.OutputLimitExceeded;
        return _failure = Result(status, diagnostic);
    }

    private void Stop()
    {
        _lifetime.Cancel();
        if (_process is not null)
        {
            try
            {
                _containment?.Terminate();
                if (!HasExited(_process)) _process.Kill(entireProcessTree: true);
                if (!_process.WaitForExit(2000))
                    throw new TimeoutException("Worker process did not exit after termination.");
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or TimeoutException)
            {
                lock (_stderr) _stderr.Append($" [process cleanup failed: {ex.Message}]");
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _containment?.Dispose();
        _process?.Dispose();
        try { _stderrTask?.GetAwaiter().GetResult(); }
        catch (ObjectDisposedException) { }
        _lifetime.Dispose();
    }

    private sealed class OutputLimitException : Exception;
}
