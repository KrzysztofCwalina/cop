// Discovers Python runtimes and exchanges bounded syntax and semantic requests.

using System.Text.Json;
using System.IO.Compression;
using System.Text;
using Cop.Core;

namespace Cop.Providers.SourceParsers;

internal sealed class PythonToolchain
{
    private sealed record Interpreter(string Path, string[] Prefix, string Version);
    private readonly Lazy<Interpreter?> _interpreter;
    private static readonly string Script = LoadScript();

    /// <summary>
    /// Configures Python discovery and whether external analysis is enabled.
    /// </summary>
    public PythonToolchain(bool enabled = true, string? searchPath = null) =>
        _interpreter = new(() => enabled ? Discover(searchPath) : null, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Gets the Python runtime identity used in source cache keys.</summary>
    public string CacheIdentity
    {
        get
        {
            Interpreter? interpreter = _interpreter.Value;
            if (interpreter is null)
            {
                WarnFallback();
            }

            return interpreter is null ? "simpler" : $"{interpreter.Path}:{interpreter.Version}";
        }
    }

    private static void WarnFallback() => ProviderWarnings.ReportOnce("python", "toolchain",
        "A usable Python 3 interpreter was not found. Using simpler Python analysis; syntax and structural facts may be incomplete.");

    /// <summary>
    /// Requests Python syntax or semantic analysis for the supplied source.
    /// </summary>
    public JsonDocument? Analyze(string path, string source, string mode = "source", int? line = null,
        int? byteColumn = null, int? endLine = null, int? endByteColumn = null)
    {
        Interpreter? interpreter = _interpreter.Value;
        if (interpreter is null)
        {
            WarnFallback();
            return null;
        }
        string request = JsonSerializer.Serialize(new { path, source, mode, line, byteColumn, endLine, endByteColumn });
        ToolchainResult result = ToolchainExecutionScope.RequestAsync(interpreter.Path,
            [.. interpreter.Prefix, "-I", "-S", "-c", Script], Environment.CurrentDirectory, request,
            timeout: TimeSpan.FromSeconds(15), maxOutputCharacters: 16_777_216).GetAwaiter().GetResult();
        if (result.Status != ToolchainStatus.Success)
        {
            throw new InvalidOperationException($"Python analysis failed for '{path}': {result.Diagnostic} {result.StandardError}".Trim());
        }

        try
        {
            return JsonDocument.Parse(result.StandardOutput, new JsonDocumentOptions { MaxDepth = 512 });
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Python analysis returned invalid JSON for '{path}': {ex.Message}", ex);
        }
    }

    private static Interpreter? Discover(string? searchPath)
    {
        foreach (string? name in new[] { "python", "python3", "py" })
        {
            string? path = ToolchainProcess.FindExecutable(name, searchPath);
            if (path is null)
            {
                continue;
            }

            string[] prefix = name == "py" ? ["-3"] : [];
            ToolchainResult probe = ToolchainProcess.RunAsync(path,
                [.. prefix, "-I", "-S", "-c", "import ast,sys,base64,zlib; print('cop-python-ast\\n' + sys.version + '\\n' + sys.executable if sys.version_info >= (3,8) else 'unsupported')"],
                Environment.CurrentDirectory, timeout: TimeSpan.FromSeconds(2), maxOutputCharacters: 4096)
                .GetAwaiter().GetResult();
            if (probe.Status == ToolchainStatus.Success && probe.StandardOutput.Replace("\r\n", "\n").StartsWith("cop-python-ast\n", StringComparison.Ordinal))
            {
                return new(path, prefix, probe.StandardOutput.Trim());
            }
        }
        return null;
    }

    private static string LoadScript()
    {
        string Read(string name)
        {
            using Stream stream = typeof(PythonToolchain).Assembly.GetManifestResourceStream("Cop.Providers.src." + name)
                ?? throw new InvalidOperationException($"The embedded Python helper '{name}' is missing.");
            using StreamReader reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        byte[] source = Encoding.UTF8.GetBytes(Read("PythonBinding.py") + "\n" + Read("PythonAst.py"));
        using MemoryStream buffer = new MemoryStream();
        using (ZLibStream compressed = new ZLibStream(buffer, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            compressed.Write(source);
        }
        // Keep the trusted helper below Windows' command-line limit as analysis grows.
        return "import base64,zlib;exec(compile(zlib.decompress(base64.b64decode('"
            + Convert.ToBase64String(buffer.ToArray()) + "')),'<cop-python-helper>','exec'))";
    }
}
