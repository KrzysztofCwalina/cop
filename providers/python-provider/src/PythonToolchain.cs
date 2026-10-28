using System.Text.Json;
using Cop.Core;

namespace Cop.Providers.SourceParsers;

internal sealed class PythonToolchain
{
    private sealed record Interpreter(string Path, string[] Prefix, string Version);
    private readonly Lazy<Interpreter?> _interpreter;
    private static readonly string Script = LoadScript();

    public PythonToolchain(bool enabled = true, string? searchPath = null) =>
        _interpreter = new(() => enabled ? Discover(searchPath) : null, LazyThreadSafetyMode.ExecutionAndPublication);

    public string CacheIdentity
    {
        get
        {
            var interpreter = _interpreter.Value;
            if (interpreter is null) WarnFallback();
            return interpreter is null ? "simpler" : $"{interpreter.Path}:{interpreter.Version}";
        }
    }

    private static void WarnFallback() => ProviderWarnings.ReportOnce("python", "toolchain",
        "A usable Python 3 interpreter was not found. Using simpler Python analysis; syntax and structural facts may be incomplete.");

    public JsonDocument? Analyze(string path, string source, string mode = "source")
    {
        var interpreter = _interpreter.Value;
        if (interpreter is null)
        {
            WarnFallback();
            return null;
        }
        var request = JsonSerializer.Serialize(new { path, source, mode });
        var result = ToolchainExecutionScope.RequestAsync(interpreter.Path,
            [.. interpreter.Prefix, "-I", "-S", "-c", Script], Environment.CurrentDirectory, request,
            timeout: TimeSpan.FromSeconds(15), maxOutputCharacters: 16_777_216).GetAwaiter().GetResult();
        if (result.Status != ToolchainStatus.Success)
            throw new InvalidOperationException($"Python analysis failed for '{path}': {result.Diagnostic} {result.StandardError}".Trim());
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
        foreach (var name in new[] { "python", "python3", "py" })
        {
            var path = ToolchainProcess.FindExecutable(name, searchPath);
            if (path is null) continue;
            string[] prefix = name == "py" ? ["-3"] : [];
            var probe = ToolchainProcess.RunAsync(path,
                [.. prefix, "-I", "-S", "-c", "import ast,sys; print('cop-python-ast\\n' + sys.version + '\\n' + sys.executable if sys.version_info >= (3,8) else 'unsupported')"],
                Environment.CurrentDirectory, timeout: TimeSpan.FromSeconds(2), maxOutputCharacters: 4096)
                .GetAwaiter().GetResult();
            if (probe.Status == ToolchainStatus.Success && probe.StandardOutput.Replace("\r\n", "\n").StartsWith("cop-python-ast\n", StringComparison.Ordinal))
                return new(path, prefix, probe.StandardOutput.Trim());
        }
        return null;
    }

    private static string LoadScript()
    {
        using var stream = typeof(PythonToolchain).Assembly.GetManifestResourceStream("Cop.Providers.src.PythonAst.py")
            ?? throw new InvalidOperationException("The embedded Python AST helper is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
