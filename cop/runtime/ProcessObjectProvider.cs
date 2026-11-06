// Loads provider collections from an external process through the provider protocol.

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Cop.Core;

namespace Cop.Providers;

/// <summary>
/// A DataProvider adapter that communicates with an external process (Node.js, Python, etc.)
/// via stdin/stdout using LSP-style length-prefixed JSON messages.
///
/// Protocol framing:
///   Content-Length: {byteCount}\r\n
///   \r\n
///   {json_payload}
///
/// The provider process is launched on first use and stays alive for the session.
/// </summary>
public sealed class ProcessObjectProvider : DataProvider, IAsyncDisposable
{
    private readonly string _runtime;     // e.g., "node", "python"
    private readonly string _entryScript; // e.g., "src/index.js"
    private readonly string _workingDir;  // package directory
    private Process? _process;
    private readonly SemaphoreSlim _requests = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _disposeGate = new();
    private Task? _disposeTask;
    private ReadOnlyMemory<byte> _schema;
    private volatile bool _disposed;

    /// <summary>Timeout for provider responses (default 900s for large repo support).</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(900);

    /// <summary>Collected stderr output for diagnostics.</summary>
    private readonly StringBuilder _stderr = new();

    /// <summary>Configures a provider process that starts lazily on its first request.</summary>
    public ProcessObjectProvider(string runtime, string entryScript, string workingDir)
    {
        _runtime = runtime;
        _entryScript = entryScript;
        _workingDir = workingDir;
    }

    /// <inheritdoc />
    public override ReadOnlyMemory<byte> GetSchema()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_schema.IsEmpty)
        {
            throw new InvalidOperationException(
                "Initialize the process provider asynchronously before reading its schema.");
        }
        return _schema;
    }

    /// <summary>Loads the process schema before synchronous metadata registration.</summary>
    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (_schema.IsEmpty)
        {
            _schema = await SendRequestAsync("""{"method":"getSchema"}""", cancellationToken);
        }
    }

    /// <inheritdoc />
    public override async ValueTask<object?> QueryAsync(
        ProviderQuery query, CancellationToken cancellationToken = default)
    {
        string request = BuildQueryRequest(query);
        return await SendRequestAsync(request, cancellationToken);
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return $"Process({_runtime})";
    }

    /// <summary>
    /// Sends a JSON request to the provider process and returns the JSON response.
    /// Ensures the process is running, writes the length-prefixed message, and reads the response.
    /// </summary>
    private async ValueTask<byte[]> SendRequestAsync(
        string jsonRequest, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using CancellationTokenSource deadline =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, _lifetime.Token);
        deadline.CancelAfter(Timeout);
        bool entered = false;
        bool completed = false;
        try
        {
            await _requests.WaitAsync(deadline.Token);
            entered = true;
            EnsureProcessRunning();
            Process process = _process!;
            Task<byte[]> exchange = ExchangeFrameAsync(
                process, jsonRequest, deadline.Token).AsTask();
            try
            {
                byte[] response = await exchange.WaitAsync(deadline.Token);
                completed = true;
                return response;
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                // Windows anonymous-pipe I/O may not honor cancellation until its process exits.
                await StopProcessAsync();
                try
                {
                    await exchange;
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or
                    OperationCanceledException)
                {
                    throw new OperationCanceledException(
                        "Provider exchange was interrupted.", ex, deadline.Token);
                }
                throw;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }
        catch (OperationCanceledException ex) when (
            !cancellationToken.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Provider process ({_runtime}) did not respond within " +
                $"{Timeout.TotalSeconds}s. Stderr: {GetStderr()}", ex);
        }
        finally
        {
            if (entered)
            {
                try
                {
                    if (!completed)
                    {
                        await StopProcessAsync();
                    }
                }
                finally
                {
                    _requests.Release();
                }
            }
        }
    }

    private async ValueTask<byte[]> ExchangeFrameAsync(
        Process process, string jsonRequest, CancellationToken cancellationToken)
    {
        byte[] requestBytes = Encoding.UTF8.GetBytes(jsonRequest);
        byte[] header = Encoding.ASCII.GetBytes(
            $"Content-Length: {requestBytes.Length}\r\n\r\n");
        await process.StandardInput.BaseStream.WriteAsync(header, cancellationToken);
        await process.StandardInput.BaseStream.WriteAsync(requestBytes, cancellationToken);
        await process.StandardInput.BaseStream.FlushAsync(cancellationToken);
        return await ReadFramedMessageAsync(
            process.StandardOutput.BaseStream, cancellationToken);
    }

    /// <summary>
    /// Reads a length-prefixed message from the provider's stdout.
    /// Expects: Content-Length: N\r\n\r\n{json}
    /// </summary>
    private async ValueTask<byte[]> ReadFramedMessageAsync(
        Stream reader, CancellationToken cancellationToken)
    {
        int contentLength = -1;

        // Read headers until empty line
        while (true)
        {
            string? line = await ReadHeaderLineAsync(reader, cancellationToken);
            if (line is null)
            {
                throw new InvalidOperationException($"Provider process ({_runtime}) closed stdout unexpectedly. Stderr: {GetStderr()}");
            }

            if (line.Length == 0)
            {
                break; // End of headers
            }

            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            {
                string value = line["Content-Length:".Length..].Trim();
                if (!int.TryParse(value, out contentLength))
                {
                    throw new InvalidOperationException($"Provider process ({_runtime}) sent invalid Content-Length: '{value}'");
                }
            }
        }

        if (contentLength < 0)
        {
            throw new InvalidOperationException($"Provider process ({_runtime}) did not send Content-Length header. Stderr: {GetStderr()}");
        }

        // Read exactly contentLength bytes
        byte[] buffer = new byte[contentLength];
        int totalRead = 0;
        while (totalRead < contentLength)
        {
            int read = await reader.ReadAsync(
                buffer.AsMemory(totalRead), cancellationToken);
            if (read == 0)
            {
                throw new InvalidOperationException($"Provider process ({_runtime}) closed stdout before sending full response. Stderr: {GetStderr()}");
            }

            totalRead += read;
        }

        return buffer;
    }

    /// <summary>
    /// Reads an ASCII framing header without decoding any UTF-8 payload bytes.
    /// </summary>
    private static async ValueTask<string?> ReadHeaderLineAsync(
        Stream reader, CancellationToken cancellationToken)
    {
        List<byte> bytes = [];
        byte[] next = new byte[1];
        while (true)
        {
            int read = await reader.ReadAsync(next, cancellationToken);
            if (read == 0)
            {
                return bytes.Count == 0 ? null : Encoding.ASCII.GetString(bytes.ToArray());
            }
            if (next[0] == '\n')
            {
                return Encoding.ASCII.GetString(bytes.ToArray()).TrimEnd('\r');
            }
            bytes.Add(next[0]);
        }
    }

    private void EnsureProcessRunning()
    {
        if (_process is not null && !_process.HasExited)
        {
            return;
        }

        _process?.Dispose();
        lock (_stderr)
        {
            _stderr.Clear();
        }

        ProcessStartInfo psi = new ProcessStartInfo
        {
            FileName = _runtime,
            WorkingDirectory = _workingDir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add(_entryScript);

        try
        {
            _process = Process.Start(psi)
                ?? throw new InvalidOperationException($"Failed to start provider process: {_runtime} {_entryScript}");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException(
                $"Cannot start provider process. Is '{_runtime}' installed and on PATH? Error: {ex.Message}", ex);
        }

        // Capture stderr asynchronously for diagnostics
        _process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (_stderr)
                {
                    _stderr.AppendLine(e.Data);
                }
            }
        };
        _process.BeginErrorReadLine();
    }

    private string GetStderr()
    {
        lock (_stderr)
        {
            string text = _stderr.ToString().Trim();
            return text.Length > 0 ? text : "(no stderr output)";
        }
    }

    private static string BuildQueryRequest(ProviderQuery query)
    {
        using MemoryStream stream = new MemoryStream();
        using Utf8JsonWriter writer = new Utf8JsonWriter(stream);
        writer.WriteStartObject();
        writer.WriteString("method", "query");

        writer.WriteStartObject("params");

        if (query.RootPath is not null)
        {
            writer.WriteString("rootPath", query.RootPath);
        }

        if (query.Collection is not null)
        {
            writer.WriteString("collection", query.Collection);
        }

        JsonSerializerOptions filterOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        if (query.Filter is not null)
        {
            writer.WritePropertyName("filter");
            JsonSerializer.Serialize(writer, query.Filter, filterOptions);
        }
        if (query.CollectionFilters is not null)
        {
            writer.WritePropertyName("collectionFilters");
            JsonSerializer.Serialize(writer, query.CollectionFilters, filterOptions);
        }

        if (query.ExcludedDirectories is not null)
        {
            writer.WriteStartArray("excludedDirectories");
            foreach (string d in query.ExcludedDirectories)
            {
                writer.WriteStringValue(d);
            }

            writer.WriteEndArray();
        }

        if (query.Options is not null)
        {
            writer.WriteStartObject("options");
            foreach ((string? k, string? v) in query.Options)
            {
                writer.WriteString(k, v);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndObject(); // params
        writer.WriteEndObject(); // root

        writer.Flush();
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Closes provider input and terminates a process that does not exit promptly.</summary>
    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
        {
            _disposed = true;
            return new ValueTask(_disposeTask ??= DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        await _lifetime.CancelAsync();
        await _requests.WaitAsync();
        try
        {
            if (_process is { } process && !process.HasExited)
            {
                process.StandardInput.Close();
                Task exit = process.WaitForExitAsync();
                if (await Task.WhenAny(exit, Task.Delay(TimeSpan.FromSeconds(3))) == exit)
                {
                    await exit;
                }
            }
        }
        finally
        {
            try
            {
                await StopProcessAsync();
            }
            finally
            {
                _requests.Release();
                _requests.Dispose();
                _lifetime.Dispose();
            }
        }
    }

    private async ValueTask StopProcessAsync()
    {
        Process? process = _process;
        _process = null;
        if (process is null)
        {
            return;
        }
        using (process)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }
}
