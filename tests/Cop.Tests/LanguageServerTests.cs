using System.Text;
using System.Text.Json.Nodes;
using Cop.Cli.Lsp;
using Cop.Lang;
using NUnit.Framework;

namespace Cop.Tests;

/// <summary>
/// Exercises the LSP server end-to-end over in-memory streams: JSON-RPC framing, the
/// initialize handshake, document sync, and that a didOpen of a broken document produces a
/// publishDiagnostics notification carrying the real compiler's error. Plus unit coverage of the
/// CopDiagnostic -> LSP diagnostic conversion (1-based compiler positions -> 0-based LSP ranges).
/// </summary>
[TestFixture]
public class LanguageServerTests
{
    private static byte[] Frame(JsonObject message)
    {
        var body = Encoding.UTF8.GetBytes(message.ToJsonString());
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        return [.. header, .. body];
    }

    private static List<JsonObject> ParseFrames(byte[] data)
    {
        var frames = new List<JsonObject>();
        int pos = 0;
        var sep = "\r\n\r\n"u8.ToArray();
        while (pos < data.Length)
        {
            int headerEnd = IndexOf(data, sep, pos);
            if (headerEnd < 0) break;
            var headerText = Encoding.ASCII.GetString(data, pos, headerEnd - pos);
            int len = -1;
            foreach (var line in headerText.Split("\r\n"))
            {
                int c = line.IndexOf(':');
                if (c > 0 && line[..c].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    int.TryParse(line[(c + 1)..].Trim(), out len);
            }
            int bodyStart = headerEnd + sep.Length;
            if (len < 0 || bodyStart + len > data.Length) break;
            var body = Encoding.UTF8.GetString(data, bodyStart, len);
            if (JsonNode.Parse(body) is JsonObject obj) frames.Add(obj);
            pos = bodyStart + len;
        }
        return frames;
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int start)
    {
        for (int i = start; i <= haystack.Length - needle.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { match = false; break; }
            }
            if (match) return i;
        }
        return -1;
    }

    private static string PathToUri(string path) => new Uri(Path.GetFullPath(path)).AbsoluteUri;

    [Test]
    public void Server_InitializeHandshake_ReturnsTextDocumentSyncCapability()
    {
        var input = new MemoryStream();
        var initialize = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "initialize",
            ["params"] = new JsonObject()
        };
        var shutdown = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 2, ["method"] = "shutdown" };
        var exit = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "exit" };
        WriteAll(input, Frame(initialize), Frame(shutdown), Frame(exit));
        input.Position = 0;
        var output = new MemoryStream();

        int code = new CopLanguageServer(input, output).Run();

        Assert.That(code, Is.EqualTo(0), "exit after shutdown must return 0");
        var frames = ParseFrames(output.ToArray());
        var initResult = frames.FirstOrDefault(f => f["id"]?.GetValue<int>() == 1);
        Assert.That(initResult, Is.Not.Null, "must respond to initialize");
        var sync = initResult!["result"]?["capabilities"]?["textDocumentSync"];
        Assert.That(sync, Is.Not.Null, "must advertise textDocumentSync");
        Assert.That(sync!["openClose"]?.GetValue<bool>(), Is.True);
        Assert.That(sync["change"]?.GetValue<int>(), Is.EqualTo(1), "full document sync");
    }

    [Test]
    public void Server_DidOpenBrokenDocument_PublishesCompilerDiagnostic()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cop-langsrv-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "bad.cop");
            var text = "let x = undefinedThing\n";
            File.WriteAllText(file, text);
            var uri = PathToUri(file);

            var input = new MemoryStream();
            var initialize = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "initialize", ["params"] = new JsonObject() };
            var didOpen = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = "textDocument/didOpen",
                ["params"] = new JsonObject
                {
                    ["textDocument"] = new JsonObject
                    {
                        ["uri"] = uri,
                        ["languageId"] = "cop",
                        ["version"] = 1,
                        ["text"] = text
                    }
                }
            };
            var exit = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "exit" };
            WriteAll(input, Frame(initialize), Frame(didOpen), Frame(exit));
            input.Position = 0;
            var output = new MemoryStream();

            new CopLanguageServer(input, output).Run();

            var frames = ParseFrames(output.ToArray());
            var publish = frames.FirstOrDefault(f =>
                f["method"]?.GetValue<string>() == "textDocument/publishDiagnostics");
            Assert.That(publish, Is.Not.Null, "didOpen must trigger publishDiagnostics");

            Assert.That(publish!["params"]?["uri"]?.GetValue<string>(), Is.EqualTo(uri));
            var diags = publish["params"]?["diagnostics"] as JsonArray;
            Assert.That(diags, Is.Not.Null);
            Assert.That(diags!.Count, Is.EqualTo(1), "exactly one diagnostic");

            var d = diags[0]!;
            Assert.That(d["severity"]?.GetValue<int>(), Is.EqualTo(1), "Error == 1 in LSP");
            Assert.That(d["source"]?.GetValue<string>(), Is.EqualTo("cop"));
            Assert.That(d["message"]?.GetValue<string>(), Is.EqualTo("Undefined variable 'undefinedThing'"));
            Assert.That(d["range"]?["start"]?["line"]?.GetValue<int>(), Is.EqualTo(0), "1-based line 1 -> 0-based 0");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Test]
    public void Server_DidOpenCleanDocument_PublishesEmptyDiagnostics()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cop-langsrv-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "ok.cop");
            var text = "let greeting = 'hello'\n";
            File.WriteAllText(file, text);
            var uri = PathToUri(file);

            var input = new MemoryStream();
            var initialize = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "initialize", ["params"] = new JsonObject() };
            var didOpen = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = "textDocument/didOpen",
                ["params"] = new JsonObject
                {
                    ["textDocument"] = new JsonObject { ["uri"] = uri, ["languageId"] = "cop", ["version"] = 1, ["text"] = text }
                }
            };
            var exit = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "exit" };
            WriteAll(input, Frame(initialize), Frame(didOpen), Frame(exit));
            input.Position = 0;
            var output = new MemoryStream();

            new CopLanguageServer(input, output).Run();

            var frames = ParseFrames(output.ToArray());
            var publish = frames.FirstOrDefault(f =>
                f["method"]?.GetValue<string>() == "textDocument/publishDiagnostics");
            Assert.That(publish, Is.Not.Null);
            var diags = publish!["params"]?["diagnostics"] as JsonArray;
            Assert.That(diags!.Count, Is.EqualTo(0), "a clean document must publish zero diagnostics");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ---- ToLspDiagnostic conversion ---------------------------------------------------

    [Test]
    public void ToLspDiagnostic_NoColumn_UnderlinesWholeLine()
    {
        var text = "a\nbb\nccc\n";
        var d = new CopDiagnostic(CopDiagnosticSeverity.Error, "msg", "f.cop", Line: 3);

        var lsp = CopLanguageServer.ToLspDiagnostic(d, text);

        Assert.That(lsp["range"]!["start"]!["line"]!.GetValue<int>(), Is.EqualTo(2));
        Assert.That(lsp["range"]!["start"]!["character"]!.GetValue<int>(), Is.EqualTo(0));
        Assert.That(lsp["range"]!["end"]!["character"]!.GetValue<int>(), Is.EqualTo(3), "line 'ccc' length");
        Assert.That(lsp["severity"]!.GetValue<int>(), Is.EqualTo(1));
    }

    [Test]
    public void ToLspDiagnostic_WithColumnAndLength_MapsToZeroBasedRange()
    {
        var d = new CopDiagnostic(CopDiagnosticSeverity.Warning, "w", "f.cop", Line: 2, Column: 5, Length: 4);

        var lsp = CopLanguageServer.ToLspDiagnostic(d, "ignored");

        Assert.That(lsp["range"]!["start"]!["line"]!.GetValue<int>(), Is.EqualTo(1));
        Assert.That(lsp["range"]!["start"]!["character"]!.GetValue<int>(), Is.EqualTo(4), "1-based column 5 -> 0-based 4");
        Assert.That(lsp["range"]!["end"]!["character"]!.GetValue<int>(), Is.EqualTo(8));
        Assert.That(lsp["severity"]!.GetValue<int>(), Is.EqualTo(2), "Warning == 2 in LSP");
    }

    [Test]
    public void ToLspDiagnostic_Info_MapsToSeverityThree()
    {
        var d = new CopDiagnostic(CopDiagnosticSeverity.Info, "i", "f.cop", Line: 1);
        var lsp = CopLanguageServer.ToLspDiagnostic(d, "x");
        Assert.That(lsp["severity"]!.GetValue<int>(), Is.EqualTo(3));
    }

    private static void WriteAll(MemoryStream stream, params byte[][] chunks)
    {
        foreach (var c in chunks) stream.Write(c, 0, c.Length);
    }
}
