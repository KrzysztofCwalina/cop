// Exchanges versioned rust-analyzer requests and attaches semantic source facts.

using System.Text;
using System.Text.Json;
using Cop.Core;
using Cop.Providers.SourceModel;

namespace Cop.Providers.SourceParsers;

/// <summary>Protocol client for Cop's pinned, source-only rust-analyzer frontend.</summary>
internal sealed class RustFrontend(string executable, string root, string? standardLibraryDirectory = null,
    Func<string>? prepareStandardLibrary = null)
{
    /// <summary>Identifies the pinned frontend version and request protocol.</summary>
    public const string ProtocolIdentity = "rust-analyzer:0.0.358:protocol1";
    /// <summary>Gets the absolute path of the frontend executable.</summary>
    public string Executable { get; } = Path.GetFullPath(executable);

    /// <summary>
    /// Sends a frontend request and validates the response protocol identity.
    /// </summary>
    public JsonElement Request(object request, TimeSpan? timeout = null)
    {
        ToolchainResult result = ToolchainExecutionScope.RequestAsync(Executable, [], Path.GetFullPath(root),
            JsonSerializer.Serialize(request), timeout: timeout ?? TimeSpan.FromSeconds(60),
            maxOutputCharacters: 32 * 1024 * 1024).GetAwaiter().GetResult();
        if (result.Status != ToolchainStatus.Success)
        {
            throw new InvalidDataException($"Rust frontend {result.Status}: {result.Diagnostic} {result.StandardError}");
        }

        using JsonDocument document = JsonDocument.Parse(result.StandardOutput);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Rust frontend returned a non-object response.");
        }

        if (document.RootElement.TryGetProperty("error", out JsonElement error))
        {
            throw new InvalidDataException($"Rust frontend: {error.GetString()}");
        }

        if (!document.RootElement.TryGetProperty("protocol", out JsonElement protocol) || protocol.ValueKind != JsonValueKind.Number
            || !protocol.TryGetInt32(out int protocolVersion) || protocolVersion != 1
            || !document.RootElement.TryGetProperty("version", out JsonElement version)
            || version.ValueKind != JsonValueKind.String || version.GetString() != "0.0.358")
        {
            throw new InvalidDataException($"Rust frontend protocol/version mismatch; expected {ProtocolIdentity}.");
        }

        return document.RootElement.Clone();
    }

    /// <summary>
    /// Parses Rust source and attaches deferred project semantic analysis.
    /// </summary>
    public SourceFile Parse(string path, string text)
    {
        JsonElement response = Request(new { op = "parse", text });
        SourceFile file = new RustAstProjection(path, text, response).Build();
        Lazy<bool> analysis = new Lazy<bool>(() => { Bind(file); return true; });
        foreach (StatementInfo statement in file.Statements)
        {
            statement.DeferSemanticAnalysis(analysis);
        }

        return file;
    }

    private void Bind(SourceFile file)
    {
        try
        {
            string[] unsupportedEnvironment = Environment.GetEnvironmentVariables().Keys.Cast<string>()
                .Where(name => name is "RUSTFLAGS" or "CARGO_ENCODED_RUSTFLAGS" or "CARGO_BUILD_TARGET" ||
                    name.StartsWith("CARGO_TARGET_", StringComparison.Ordinal) && name.EndsWith("_RUSTFLAGS", StringComparison.Ordinal))
                .Where(name => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name))).ToArray();
            if (unsupportedEnvironment.Length != 0)
            {
                ProviderWarnings.ReportOnce("rust", root,
                    $"Unsupported Cargo environment context ({string.Join(", ", unsupportedEnvironment)}); semantic bindings left unknown rather than assuming host defaults.");
                return;
            }
            JsonElement selection = Request(new { op = "toolchain", file = Path.GetFullPath(file.Path) });
            if (Text(selection, "unsupported") is { } unsupported)
            {
                ProviderWarnings.ReportOnce("rust", root, unsupported);
                return;
            }
            string? channel = Text(selection, "channel");
            string compiler = channel is null ? "rustc" : "rustup";
            string[] prefix = channel is null ? [] : ["run", channel, "rustc"];
            ToolchainResult cfg = ToolchainProcess.RunAsync(compiler, [.. prefix, "--print", "cfg"], Path.GetTempPath(),
                environment: new Dictionary<string, string?> { ["RUSTUP_AUTO_INSTALL"] = "0" }).GetAwaiter().GetResult();
            ToolchainResult version = ToolchainProcess.RunAsync(compiler, [.. prefix, "-vV"], Path.GetTempPath(),
                environment: new Dictionary<string, string?> { ["RUSTUP_AUTO_INSTALL"] = "0" }).GetAwaiter().GetResult();
            if (cfg.Status != ToolchainStatus.Success || version.Status != ToolchainStatus.Success)
            {
                ProviderWarnings.ReportOnce("rust", root, $"Rust target configuration unavailable: {cfg.Diagnostic} {version.Diagnostic}; target-specific context is partial.");
                if (channel is not null)
                {
                    return;
                }
            }
            ToolchainResult sysroot = ToolchainProcess.RunAsync(compiler, [.. prefix, "--print", "sysroot"], Path.GetTempPath(),
                environment: new Dictionary<string, string?> { ["RUSTUP_AUTO_INSTALL"] = "0" }).GetAwaiter().GetResult();
            string? library = SelectStandardLibrary(version.StandardOutput,
                sysroot.Status == ToolchainStatus.Success ? sysroot.StandardOutput.Trim() : null);
            JsonElement response = Request(new { op = "project", root = Path.GetFullPath(root),
                file = Path.GetFullPath(file.Path),
                expected_text = file.RawText,
                cfg = cfg.Status == ToolchainStatus.Success ? cfg.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : [],
                target = version.StandardOutput.Split('\n').FirstOrDefault(s => s.StartsWith("host: ", StringComparison.Ordinal))?[6..].Trim() ?? "",
                stdlib = library ?? "",
                cargo_home = Environment.GetEnvironmentVariable("CARGO_HOME")
                    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cargo") },
                timeout: TimeSpan.FromMinutes(3));
            if (response.TryGetProperty("diagnostics", out JsonElement diagnostics))
            {
                foreach (JsonElement diagnostic in diagnostics.EnumerateArray())
                {
                    ProviderWarnings.ReportOnce("rust", root, diagnostic.GetString()!);
                }
            }

            RustSourcePositions positions = new RustSourcePositions(file.RawText);
            Dictionary<(int Line, int? Column, int? EndLine, int? EndColumn), List<StatementInfo>> statements = file.Statements.GroupBy(s => (s.Line, s.Column, s.EndLine, s.EndColumn))
                .ToDictionary(g => g.Key, g => g.ToList());
            foreach (JsonElement fact in response.GetProperty("facts").EnumerateArray())
            {
                (int Line, int Column, int EndLine, int EndColumn) span = positions.Span(fact.GetProperty("start").GetInt32(), fact.GetProperty("end").GetInt32());
                if (!statements.TryGetValue(span, out List<StatementInfo>? matches))
                {
                    continue;
                }

                foreach (StatementInfo? statement in matches)
                {
                    statement.ReceiverType = Type(fact, "receiver_type");
                    statement.ValueType = Type(fact, "value_type");
                    if (fact.GetProperty("callable") is { ValueKind: JsonValueKind.Object } callable)
                    {
                        if (callable.GetProperty("parameters").EnumerateArray().Any(p => p.ValueKind != JsonValueKind.String))
                        {
                            ProviderWarnings.ReportOnce("rust", root,
                                $"Partial semantic context: parameter type unavailable for '{Text(callable, "name")}'; callable identity is left unknown.");
                            continue;
                        }

                        string? sourcePath = Text(callable, "source_file");
                        JsonElement declaration = callable.GetProperty("span");
                        statement.ReferencedCallable = new(Text(callable, "name")!, Text(callable, "signature")!)
                        {
                            DeclaringType = Text(callable, "declaring_type"), Namespace = Text(callable, "namespace"),
                            ReturnType = Type(callable, "return_type"),
                            Parameters = callable.GetProperty("parameters").EnumerateArray()
                                .Select(p => ParseType(p.GetString()!)).ToList(),
                            SourceFile = sourcePath,
                            Line = declaration.ValueKind == JsonValueKind.Object ? declaration.GetProperty("line").GetInt32() : null,
                            Column = declaration.ValueKind == JsonValueKind.Object ? declaration.GetProperty("column").GetInt32() : null,
                            EndLine = declaration.ValueKind == JsonValueKind.Object ? declaration.GetProperty("end_line").GetInt32() : null,
                            EndColumn = declaration.ValueKind == JsonValueKind.Object ? declaration.GetProperty("end_column").GetInt32() : null
                        };
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        {
            ProviderErrors.Report($"{file.Path}: error: {ex.Message}");
        }
    }

    private string? SelectStandardLibrary(string compilerVersion, string? sysroot)
    {
        if (standardLibraryDirectory is null && sysroot is not null)
        {
            string installed = Path.Combine(sysroot, "lib", "rustlib", "src", "rust", "library");
            if (File.Exists(Path.Combine(installed, "core", "src", "lib.rs")))
            {
                return installed;
            }
        }
        string? release = compilerVersion.Split('\n')
            .FirstOrDefault(s => s.StartsWith("release: ", StringComparison.Ordinal))?[9..].Trim();
        string? commit = compilerVersion.Split('\n')
            .FirstOrDefault(s => s.StartsWith("commit-hash: ", StringComparison.Ordinal))?[13..].Trim();
        if (string.IsNullOrEmpty(release) || string.IsNullOrEmpty(commit))
        {
            ProviderWarnings.ReportOnce("rust", root,
                "Rust compiler identity is unavailable; standard-library bindings are left unknown without preparing unused sources. Local source analysis remains available.");
            return null;
        }
        string bundled = standardLibraryDirectory ?? prepareStandardLibrary?.Invoke()
            ?? Path.Combine(Path.GetDirectoryName(Executable)!, "rust-stdlib");
        string manifest = Path.Combine(bundled, "manifest.json");
        if (File.Exists(manifest))
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifest));
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("release", out JsonElement bundledRelease) || bundledRelease.ValueKind != JsonValueKind.String ||
                !document.RootElement.TryGetProperty("compilerCommit", out JsonElement bundledCommit) || bundledCommit.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException("Bundled Rust standard-library manifest is malformed.");
            }

            if (bundledRelease.GetString() == release && bundledCommit.GetString() == commit)
            {
                string library = Path.Combine(bundled, "library");
                if (!File.Exists(Path.Combine(library, "core", "src", "lib.rs")))
                {
                    throw new InvalidDataException("Bundled Rust standard-library source is incomplete.");
                }

                return library;
            }
            ProviderWarnings.ReportOnce("rust", root,
                $"Bundled Rust standard-library sources do not match installed compiler {release ?? "unknown"} ({commit ?? "unknown"}); standard-library bindings are left unknown.");
        }
        else
        {
            ProviderWarnings.ReportOnce("rust", root,
            "Matching Rust standard-library sources are unavailable; standard-library bindings are left unknown. No components are downloaded.");
        }

        return null;
    }

    private static string? Text(JsonElement element, string property) =>
        element.GetProperty(property) is { ValueKind: JsonValueKind.String } text ? text.GetString() : null;
    private static TypeReference? Type(JsonElement element, string property) =>
        Text(element, property) is { } text ? ParseType(text) : null;
    private static TypeReference ParseType(string text) => RustTypeReferences.Parse(text,
        () => throw new InvalidDataException("Rust semantic type exceeded depth limit."));
}

internal sealed class RustSourcePositions
{
    private readonly int[] _characters;
    private readonly int[] _lines;
    private readonly int[] _columns;
    /// <summary>
    /// Indexes UTF-8 byte offsets against source characters and line starts.
    /// </summary>
    public RustSourcePositions(string text)
    {
        int bytes = Encoding.UTF8.GetByteCount(text);
        _characters = new int[bytes + 1]; _lines = new int[bytes + 1]; _columns = new int[bytes + 1];
        int offset = 0, character = 0, line = 1, column = 1;
        foreach (Rune rune in text.EnumerateRunes())
        {
            for (int i = 0; i < rune.Utf8SequenceLength; i++)
            { _characters[offset + i] = character; _lines[offset + i] = line; _columns[offset + i] = column; }
            offset += rune.Utf8SequenceLength; character += rune.Utf16SequenceLength;
            if (rune.Value == '\n') { line++; column = 1; } else
            {
                column += rune.Utf16SequenceLength;
            }
        }
        _characters[bytes] = character; _lines[bytes] = line; _columns[bytes] = column;
    }
    /// <summary>Converts a frontend byte offset to its source character index.</summary>
    public int Character(int offset) => _characters[offset];
    /// <summary>
    /// Converts frontend byte offsets into a line-and-column source span.
    /// </summary>
    public (int Line, int Column, int EndLine, int EndColumn) Span(int start, int end) =>
        (_lines[start], _columns[start], _lines[end], _columns[end]);
}
