// Discovers Python runtimes and exchanges bounded syntax and semantic requests.

using System.Text.Json;
using System.IO.Compression;
using System.Text;
using Cop.Core;

namespace Cop.Providers.SourceParsers;

internal sealed class PythonToolchain
{
    private sealed record Interpreter(string Path, string[] Prefix, string Version);
    private readonly AsyncProviderValue<Interpreter?> _interpreter;
    private readonly PythonAnalysisBackend _backend;
    private string _cacheIdentity;
    private static readonly string s_script = LoadScript();

    /// <summary>
    /// Configures Python discovery and whether external analysis is enabled.
    /// </summary>
    public PythonToolchain(
        PythonAnalysisBackend backend = PythonAnalysisBackend.PreferNative,
        string? searchPath = null)
    {
        if (backend is not PythonAnalysisBackend.PreferNative
            and not PythonAnalysisBackend.SyntaxOnly)
        {
            throw new ArgumentOutOfRangeException(nameof(backend));
        }

        _backend = backend;
        _cacheIdentity = backend == PythonAnalysisBackend.PreferNative
            ? "python:pending:" + (searchPath is null ? "inherited" : "path:" + searchPath)
            : "simpler";
        _interpreter = new(async token =>
        {
            Interpreter? interpreter = backend == PythonAnalysisBackend.PreferNative
                ? await DiscoverAsync(searchPath, token).ConfigureAwait(false) : null;
            Volatile.Write(ref _cacheIdentity, interpreter is null
                ? "simpler" : $"python:{interpreter.Path}:{interpreter.Version}");
            return interpreter;
        });
    }

    /// <summary>
    /// Gets discovery configuration before analysis, then the resolved backend identity.
    /// Reading this property never launches or synchronously waits for an interpreter.
    /// </summary>
    public string CacheIdentity => Volatile.Read(ref _cacheIdentity);

    private static void WarnFallback() => ProviderWarnings.ReportOnce("python", "toolchain",
        "A usable Python 3 interpreter was not found. Using simpler Python analysis; syntax and structural facts may be incomplete.");

    /// <summary>
    /// Requests Python syntax or semantic analysis for the supplied source.
    /// </summary>
    public async ValueTask<JsonDocument?> AnalyzeAsync(
        PythonAnalysisRequest analysis, CancellationToken cancellationToken = default)
    {
        Interpreter? interpreter = await _interpreter.GetValueAsync(cancellationToken)
            .ConfigureAwait(false);
        if (interpreter is null)
        {
            if (_backend == PythonAnalysisBackend.PreferNative)
            {
                WarnFallback();
            }
            return null;
        }
        string path = analysis.Path;
        string request = JsonSerializer.Serialize(new
        {
            path, source = analysis.Source, mode = analysis.Mode, line = analysis.Line,
            byteColumn = analysis.ByteColumn, endLine = analysis.EndLine,
            endByteColumn = analysis.EndByteColumn
        });
        ToolchainResult result = await ToolchainExecutionScope.RequestAsync(
            new ToolchainCommand(interpreter.Path,
                [.. interpreter.Prefix, "-I", "-S", "-c", s_script], Environment.CurrentDirectory),
            request, new ToolchainRunOptions
            {
                Timeout = TimeSpan.FromSeconds(15), MaxOutputCharacters = 16_777_216
            }, cancellationToken).ConfigureAwait(false);
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

    private static async ValueTask<Interpreter?> DiscoverAsync(
        string? searchPath, CancellationToken cancellationToken)
    {
        foreach (string? name in new[] { "python", "python3", "py" })
        {
            string? path = ToolchainProcess.FindExecutable(name, searchPath);
            if (path is null)
            {
                continue;
            }

            string[] prefix = name == "py" ? ["-3"] : [];
            ToolchainResult probe = await ToolchainProcess.RunAsync(new ToolchainCommand(path, [.. prefix, "-I", "-S", "-c", "import ast,sys,base64,zlib; print('cop-python-ast\\n' + sys.version + '\\n' + sys.executable if sys.version_info >= (3,8) else 'unsupported')"], Environment.CurrentDirectory), new ToolchainRunOptions { Timeout = TimeSpan.FromSeconds(2), MaxOutputCharacters = 4096 },
                cancellationToken).ConfigureAwait(false);
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
