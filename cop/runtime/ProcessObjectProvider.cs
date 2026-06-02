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
public sealed class ProcessObjectProvider : DataProvider, IDisposable
{
    private readonly string _runtime;     // e.g., "node", "python"
    private readonly string _entryScript; // e.g., "src/index.js"
    private readonly string _workingDir;  // package directory
    private Process? _process;
    private readonly object _lock = new();
    private bool _disposed;

    /// <summary>Timeout for provider responses (default 900s for large repo support).</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(900);

    /// <summary>Collected stderr output for diagnostics.</summary>
    private readonly StringBuilder _stderr = new();

    public ProcessObjectProvider(string runtime, string entryScript, string workingDir)
    {
        _runtime = runtime;
        _entryScript = entryScript;
        _workingDir = workingDir;
    }

    public override ReadOnlyMemory<byte> GetSchema()
    {
        var request = """{"method":"getSchema"}""";
        var response = SendRequest(request);
        return Encoding.UTF8.GetBytes(response);
    }

    public override byte[] Query(ProviderQuery query)
    {
        var request = BuildQueryRequest(query);
        var response = SendRequest(request);
        return Encoding.UTF8.GetBytes(response);
    }

    public override string ToString()
    {
        return $"Process({_runtime})";
    }

    /// <summary>
    /// Sends a JSON request to the provider process and returns the JSON response.
    /// Ensures the process is running, writes the length-prefixed message, and reads the response.
    /// </summary>
    private string SendRequest(string jsonRequest)
    {
        lock (_lock)
        {
            EnsureProcessRunning();

            var process = _process!;
            var stdin = process.StandardInput;
            var stdout = process.StandardOutput;

            // Write request with Content-Length framing
            var requestBytes = Encoding.UTF8.GetBytes(jsonRequest);
            var header = $"Content-Length: {requestBytes.Length}\r\n\r\n";
            stdin.Write(header);
            stdin.Write(jsonRequest);
            stdin.Flush();

            // Read response with Content-Length framing
            var response = ReadFramedMessage(stdout);
            return response;
        }
    }

    /// <summary>
    /// Reads a length-prefixed message from the provider's stdout.
    /// Expects: Content-Length: N\r\n\r\n{json}
    /// </summary>
    private string ReadFramedMessage(StreamReader reader)
    {
        int contentLength = -1;

        // Read headers until empty line
        while (true)
        {
            var line = ReadLineWithTimeout(reader);
            if (line is null)
                throw new InvalidOperationException($"Provider process ({_runtime}) closed stdout unexpectedly. Stderr: {GetStderr()}");

            if (line.Length == 0)
                break; // End of headers

            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            {
                var value = line["Content-Length:".Length..].Trim();
                if (!int.TryParse(value, out contentLength))
                    throw new InvalidOperationException($"Provider process ({_runtime}) sent invalid Content-Length: '{value}'");
            }
        }

        if (contentLength < 0)
            throw new InvalidOperationException($"Provider process ({_runtime}) did not send Content-Length header. Stderr: {GetStderr()}");

        // Read exactly contentLength bytes
        var buffer = new char[contentLength];
        int totalRead = 0;
        while (totalRead < contentLength)
        {
            var read = reader.Read(buffer, totalRead, contentLength - totalRead);
            if (read == 0)
                throw new InvalidOperationException($"Provider process ({_runtime}) closed stdout before sending full response. Stderr: {GetStderr()}");
            totalRead += read;
        }

        return new string(buffer, 0, totalRead);
    }

    /// <summary>
    /// Reads a line from the stream with timeout support.
    /// </summary>
    private string? ReadLineWithTimeout(StreamReader reader)
    {
        var task = Task.Run(() => reader.ReadLine());
        if (task.Wait(Timeout))
            return task.Result;
        throw new TimeoutException($"Provider process ({_runtime}) did not respond within {Timeout.TotalSeconds}s. Stderr: {GetStderr()}");
    }

    private void EnsureProcessRunning()
    {
        if (_process is not null && !_process.HasExited)
            return;

        _process?.Dispose();
        _stderr.Clear();

        var psi = new ProcessStartInfo
        {
            FileName = _runtime,
            Arguments = _entryScript,
            WorkingDirectory = _workingDir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

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
                _stderr.AppendLine(e.Data);
        };
        _process.BeginErrorReadLine();
    }

    private string GetStderr()
    {
        var text = _stderr.ToString().Trim();
        return text.Length > 0 ? text : "(no stderr output)";
    }

    private static string BuildQueryRequest(ProviderQuery query)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);
        writer.WriteStartObject();
        writer.WriteString("method", "query");

        writer.WriteStartObject("params");

        if (query.RootPath is not null)
            writer.WriteString("rootPath", query.RootPath);

        if (query.Collection is not null)
            writer.WriteString("collection", query.Collection);

        if (query.ExcludedDirectories is not null)
        {
            writer.WriteStartArray("excludedDirectories");
            foreach (var d in query.ExcludedDirectories)
                writer.WriteStringValue(d);
            writer.WriteEndArray();
        }

        if (query.Options is not null)
        {
            writer.WriteStartObject("options");
            foreach (var (k, v) in query.Options)
                writer.WriteString(k, v);
            writer.WriteEndObject();
        }

        writer.WriteEndObject(); // params
        writer.WriteEndObject(); // root

        writer.Flush();
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_process is not null && !_process.HasExited)
        {
            try
            {
                _process.StandardInput.Close();
                if (!_process.WaitForExit(3000))
                    _process.Kill();
            }
            catch { /* best effort */ }
        }
        _process?.Dispose();
    }
}
