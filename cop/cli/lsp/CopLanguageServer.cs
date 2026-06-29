using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cop.Lang;

namespace Cop.Cli.Lsp;

/// <summary>
/// A minimal Language Server Protocol (LSP) server speaking JSON-RPC 2.0 over a byte stream
/// (stdio). It implements the document-sync + push-diagnostics slice of LSP: it tracks open
/// documents, and on every open/change runs the real compiler (<see cref="CopLanguageService"/>)
/// and publishes the resulting diagnostics. Hover/completion remain in the existing extension for
/// now; this server's job is to make squiggles/Problems come from the actual compiler so they can
/// never drift from it.
///
/// Framing follows the LSP base protocol: each message is "Content-Length: N\r\n\r\n" followed by
/// N bytes of UTF-8 JSON.
/// </summary>
internal sealed class CopLanguageServer
{
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly object _writeLock = new();
    private readonly Dictionary<string, string> _documents = new(StringComparer.Ordinal);
    private bool _shutdownRequested;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public CopLanguageServer(Stream input, Stream output)
    {
        _input = input;
        _output = output;
    }

    /// <summary>Runs the read/dispatch loop until the client sends <c>exit</c> or the stream closes.</summary>
    public int Run()
    {
        while (true)
        {
            JsonObject? msg;
            try
            {
                msg = ReadMessage();
            }
            catch (Exception ex)
            {
                Log($"failed to read message: {ex.Message}");
                continue;
            }
            if (msg is null) break; // stream closed

            try
            {
                var exitCode = Dispatch(msg);
                if (exitCode.HasValue) return exitCode.Value;
            }
            catch (Exception ex)
            {
                Log($"error handling '{msg["method"]?.GetValue<string>()}': {ex.Message}");
            }
        }
        // Stream closed without an explicit exit: 0 if shutdown was requested, else 1 per LSP.
        return _shutdownRequested ? 0 : 1;
    }

    private int? Dispatch(JsonObject msg)
    {
        var method = msg["method"]?.GetValue<string>();
        var id = msg["id"];

        switch (method)
        {
            case "initialize":
                Respond(id, BuildInitializeResult());
                break;
            case "initialized":
                break; // notification, no-op
            case "textDocument/didOpen":
                OnDidOpen(msg["params"] as JsonObject);
                break;
            case "textDocument/didChange":
                OnDidChange(msg["params"] as JsonObject);
                break;
            case "textDocument/didSave":
                OnDidSave(msg["params"] as JsonObject);
                break;
            case "textDocument/didClose":
                OnDidClose(msg["params"] as JsonObject);
                break;
            case "textDocument/hover":
                Respond(id, BuildHover(msg["params"] as JsonObject));
                break;
            case "shutdown":
                _shutdownRequested = true;
                Respond(id, null);
                break;
            case "exit":
                return _shutdownRequested ? 0 : 1;
            default:
                // Unknown request must get a response so the client does not hang; ignore unknown
                // notifications (those have no id).
                if (id is not null)
                    RespondError(id, -32601, $"method not found: {method}");
                break;
        }
        return null;
    }

    private static JsonObject BuildInitializeResult()
    {
        var version = typeof(CopLanguageServer).Assembly.GetName().Version?.ToString() ?? "0";
        return new JsonObject
        {
            ["capabilities"] = new JsonObject
            {
                // openClose so we get didOpen/didClose; change = 1 (full document sync).
                ["textDocumentSync"] = new JsonObject
                {
                    ["openClose"] = true,
                    ["change"] = 1
                },
                ["hoverProvider"] = true
            },
            ["serverInfo"] = new JsonObject
            {
                ["name"] = "cop-langserver",
                ["version"] = version
            }
        };
    }

    // ---- document sync -----------------------------------------------------------------

    private void OnDidOpen(JsonObject? p)
    {
        var td = p?["textDocument"] as JsonObject;
        var uri = td?["uri"]?.GetValue<string>();
        var text = td?["text"]?.GetValue<string>();
        if (uri is null || text is null) return;
        _documents[uri] = text;
        PublishDiagnostics(uri, text);
    }

    private void OnDidChange(JsonObject? p)
    {
        var uri = (p?["textDocument"] as JsonObject)?["uri"]?.GetValue<string>();
        var changes = p?["contentChanges"] as JsonArray;
        if (uri is null || changes is null || changes.Count == 0) return;
        // Full-sync: the last change carries the whole document text.
        var text = (changes[^1] as JsonObject)?["text"]?.GetValue<string>();
        if (text is null) return;
        _documents[uri] = text;
        PublishDiagnostics(uri, text);
    }

    private void OnDidSave(JsonObject? p)
    {
        var uri = (p?["textDocument"] as JsonObject)?["uri"]?.GetValue<string>();
        if (uri is null) return;
        if (_documents.TryGetValue(uri, out var text))
            PublishDiagnostics(uri, text);
    }

    private void OnDidClose(JsonObject? p)
    {
        var uri = (p?["textDocument"] as JsonObject)?["uri"]?.GetValue<string>();
        if (uri is null) return;
        _documents.Remove(uri);
        // Clear diagnostics for the closed document.
        PublishDiagnosticsRaw(uri, new JsonArray());
    }

    // ---- diagnostics -------------------------------------------------------------------

    private void PublishDiagnostics(string uri, string text)
    {
        var path = UriToPath(uri);
        var arr = new JsonArray();
        if (path is not null)
        {
            List<CopDiagnostic> diags;
            try
            {
                diags = CopLanguageService.Analyze(path, text);
            }
            catch (Exception ex)
            {
                Log($"analyze failed for {uri}: {ex.Message}");
                diags = [];
            }
            foreach (var d in diags)
                arr.Add(ToLspDiagnostic(d, text));
        }
        PublishDiagnosticsRaw(uri, arr);
    }

    private void PublishDiagnosticsRaw(string uri, JsonArray diagnostics)
    {
        var notif = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "textDocument/publishDiagnostics",
            ["params"] = new JsonObject
            {
                ["uri"] = uri,
                ["diagnostics"] = diagnostics
            }
        };
        WriteMessage(notif);
    }

    internal static JsonObject ToLspDiagnostic(CopDiagnostic d, string documentText)
    {
        int line = d.Line > 0 ? d.Line - 1 : 0;
        int startChar = d.Column is > 0 ? d.Column.Value - 1 : 0;
        int endChar;
        if (d.Length is > 0)
            endChar = startChar + d.Length.Value;
        else
            endChar = Math.Max(startChar + 1, LineLength(documentText, line));

        int severity = d.Severity switch
        {
            CopDiagnosticSeverity.Error => 1,
            CopDiagnosticSeverity.Warning => 2,
            CopDiagnosticSeverity.Info => 3,
            _ => 1
        };

        var message = d.Suggestion is { Length: > 0 }
            ? $"{d.Message}\n{d.Suggestion}"
            : d.Message;

        return new JsonObject
        {
            ["range"] = new JsonObject
            {
                ["start"] = new JsonObject { ["line"] = line, ["character"] = startChar },
                ["end"] = new JsonObject { ["line"] = line, ["character"] = endChar }
            },
            ["severity"] = severity,
            ["source"] = "cop",
            ["message"] = message
        };
    }

    private static int LineLength(string text, int lineIndex)
    {
        if (lineIndex < 0) return 1;
        int current = 0;
        int start = 0;
        for (int i = 0; i <= text.Length; i++)
        {
            if (i == text.Length || text[i] == '\n')
            {
                if (current == lineIndex)
                {
                    int len = i - start;
                    // Trim a trailing \r from CRLF line endings.
                    if (len > 0 && i > 0 && text[i - 1] == '\r') len--;
                    return Math.Max(1, len);
                }
                current++;
                start = i + 1;
            }
        }
        return 1;
    }

    // ---- hover ------------------------------------------------------------------------

    private JsonNode? BuildHover(JsonObject? p)
    {
        var uri = (p?["textDocument"] as JsonObject)?["uri"]?.GetValue<string>();
        var pos = p?["position"] as JsonObject;
        if (uri is null || pos is null) return null;
        int line = pos["line"]?.GetValue<int>() ?? 0;
        int character = pos["character"]?.GetValue<int>() ?? 0;

        var path = UriToPath(uri);
        if (path is null) return null;
        // Prefer the in-memory buffer; fall back to disk if the document isn't open.
        var text = _documents.TryGetValue(uri, out var t) ? t
                 : (File.Exists(path) ? File.ReadAllText(path) : null);
        if (text is null) return null;

        string? markdown;
        try
        {
            markdown = CopLanguageService.Hover(path, text, line, character);
        }
        catch (Exception ex)
        {
            Log($"hover failed for {uri}: {ex.Message}");
            return null;
        }
        if (markdown is null) return null;

        return new JsonObject
        {
            ["contents"] = new JsonObject
            {
                ["kind"] = "markdown",
                ["value"] = markdown
            }
        };
    }

    // ---- JSON-RPC framing --------------------------------------------------------------

    private JsonObject? ReadMessage()
    {
        int contentLength = -1;
        while (true)
        {
            var headerLine = ReadHeaderLine();
            if (headerLine is null) return null;     // EOF
            if (headerLine.Length == 0) break;        // blank line ends the headers
            int colon = headerLine.IndexOf(':');
            if (colon > 0)
            {
                var name = headerLine[..colon].Trim();
                var value = headerLine[(colon + 1)..].Trim();
                if (string.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase))
                    int.TryParse(value, out contentLength);
            }
        }
        if (contentLength < 0) return null;

        var buffer = new byte[contentLength];
        int read = 0;
        while (read < contentLength)
        {
            int n = _input.Read(buffer, read, contentLength - read);
            if (n <= 0) return null; // stream closed mid-message
            read += n;
        }
        var json = Encoding.UTF8.GetString(buffer);
        return JsonNode.Parse(json) as JsonObject;
    }

    private string? ReadHeaderLine()
    {
        var bytes = new List<byte>(64);
        while (true)
        {
            int b = _input.ReadByte();
            if (b == -1) return bytes.Count == 0 ? null : Encoding.ASCII.GetString([.. bytes]);
            if (b == '\n')
            {
                if (bytes.Count > 0 && bytes[^1] == (byte)'\r')
                    bytes.RemoveAt(bytes.Count - 1);
                return Encoding.ASCII.GetString([.. bytes]);
            }
            bytes.Add((byte)b);
        }
    }

    private void WriteMessage(JsonObject message)
    {
        var json = message.ToJsonString(JsonOpts);
        var body = Encoding.UTF8.GetBytes(json);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        lock (_writeLock)
        {
            _output.Write(header, 0, header.Length);
            _output.Write(body, 0, body.Length);
            _output.Flush();
        }
    }

    private void Respond(JsonNode? id, JsonNode? result)
    {
        WriteMessage(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["result"] = result
        });
    }

    private void RespondError(JsonNode? id, int code, string message)
    {
        WriteMessage(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message }
        });
    }

    private static string? UriToPath(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed)) return null;
        if (!parsed.IsFile) return null;
        return parsed.LocalPath;
    }

    private static void Log(string message) => Console.Error.WriteLine($"[cop-langserver] {message}");
}
