// Projects PowerShell parser output into script and command metadata.

using System.Text;
using System.Text.Json;
using Cop.Core;

namespace Cop.Providers.PowerShell;

internal sealed class PowerShellAstParser
{
    private readonly Func<string, string?> _find;
    private readonly Func<string, string, CancellationToken, ValueTask<ToolchainResult>> _request;
    private readonly AsyncProviderValue<bool> _initialization;
    private string? _executable;
    private string _unavailableReason = "PowerShell is not installed on PATH";

    internal PowerShellAstParser(Func<string, string?>? find = null,
        Func<string, string, CancellationToken, ValueTask<ToolchainResult>>? request = null)
    {
        _find = find ?? (name => ToolchainProcess.FindExecutable(name));
        _request = request ?? RequestAsync;
        _initialization = new(InitializeAsync);
    }

    private const string Script = """
        $ErrorActionPreference = 'Stop'
        [Console]::InputEncoding = [System.Text.UTF8Encoding]::new($false)
        [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
        while ($null -ne ($request = [Console]::In.ReadLine())) {
        $inputData = $request | ConvertFrom-Json
        if ($inputData.Probe) {
            $probeTokens = $null
            $probeErrors = $null
            $null = [System.Management.Automation.Language.Parser]::ParseInput('', [ref]$probeTokens, [ref]$probeErrors)
            @{ Ready = $true } | ConvertTo-Json -Compress
            continue
        }
        $source = $inputData.Source
        $tokens = $null
        $errors = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseInput($source, [ref]$tokens, [ref]$errors)
        $commands = @($ast.FindAll({param($n) $n -is [System.Management.Automation.Language.CommandAst]}, $true) |
            ForEach-Object {
                $name = $_.GetCommandName()
                if ($null -ne $name) {
                    $text = $_.Extent.Text
                    if ($_.Parent -is [System.Management.Automation.Language.PipelineAst]) { $text = $_.Parent.Extent.Text }
                    @{ Name = $name; Text = $text; Line = $_.Extent.StartLineNumber }
                }
            })
        $diagnostics = @($errors | ForEach-Object { "line $($_.Extent.StartLineNumber): $($_.Message)" })
        @{ Commands = $commands; Errors = $diagnostics } | ConvertTo-Json -Depth 4 -Compress
        }
        """;

    private static async ValueTask<ToolchainResult> RequestAsync(
        string executable, string request, CancellationToken cancellationToken)
    {
        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(Script));
        return await ToolchainExecutionScope.RequestAsync(new ToolchainCommand(executable, ["-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", encoded], Path.GetTempPath()), request, new ToolchainRunOptions { Timeout = TimeSpan.FromSeconds(15) }, cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<bool> InitializeAsync(CancellationToken cancellationToken)
    {
        List<string> failures = new List<string>();
        foreach (string? name in new[] { "pwsh", "powershell" })
        {
            string? executable = _find(name);
            if (executable is null)
            {
                continue;
            }

            ToolchainResult probe = await _request(executable, """{"Probe":true}""",
                cancellationToken).ConfigureAwait(false);
            string reason = probe.Diagnostic ?? probe.StandardError;
            if (probe.Status == ToolchainStatus.Success)
            {
                try
                {
                    using JsonDocument document = JsonDocument.Parse(probe.StandardOutput);
                    if (document.RootElement.ValueKind == JsonValueKind.Object
                        && document.RootElement.TryGetProperty("Ready", out JsonElement ready)
                        && ready.ValueKind == JsonValueKind.True)
                    {
                        _executable = executable;
                        return true;
                    }
                    reason = "invalid availability handshake (expected Ready=true)";
                }
                catch (JsonException ex) { reason = $"invalid availability handshake: {ex.Message}"; }
            }
            failures.Add($"{name} preflight unusable: {reason} ({probe.Status})");
        }
        if (failures.Count > 0)
        {
            _unavailableReason = string.Join("; ", failures);
        }
        return true;
    }

    internal async ValueTask<PowerShellParser.PowerShellParseResult> ParseAsync(
        string text, string path, CancellationToken cancellationToken = default)
    {
        await _initialization.GetValueAsync(cancellationToken).ConfigureAwait(false);
        if (_executable is null)
        {
            return Fallback(text, _unavailableReason);
        }

        ToolchainResult result = await _request(_executable,
            JsonSerializer.Serialize(new { Source = text }), cancellationToken).ConfigureAwait(false);
        if (result.Status != ToolchainStatus.Success)
        {
            throw new InvalidDataException($"{path}: PowerShell AST request {result.Status}: {result.Diagnostic ?? result.StandardError}");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(result.StandardOutput);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("Errors", out JsonElement errors) || errors.ValueKind != JsonValueKind.Array
                || !document.RootElement.TryGetProperty("Commands", out JsonElement commandItems) || commandItems.ValueKind != JsonValueKind.Array)
            {
                throw new JsonException("Expected Commands and Errors arrays.");
            }

            foreach (JsonElement error in document.RootElement.GetProperty("Errors").EnumerateArray())
            {
                if (error.ValueKind != JsonValueKind.String)
                {
                    throw new JsonException("Expected a diagnostic string.");
                }

                ProviderErrors.Report($"{path}: {error.GetString()}");
            }
            List<PowerShellCommandInfo> commands = new List<PowerShellCommandInfo>();
            foreach (JsonElement command in commandItems.EnumerateArray())
            {
                if (command.ValueKind != JsonValueKind.Object
                    || !command.TryGetProperty("Name", out JsonElement name) || name.ValueKind != JsonValueKind.String
                    || !command.TryGetProperty("Text", out JsonElement source) || source.ValueKind != JsonValueKind.String
                    || !command.TryGetProperty("Line", out JsonElement line) || line.ValueKind != JsonValueKind.Number
                    || !line.TryGetInt32(out int number) || number < 1)
                {
                    throw new JsonException("Expected command Name, Text and positive Line.");
                }

                commands.Add(new(name.GetString()!, source.GetString()!, number));
            }
            return new(commands, new(commands.Any(c => c.Name.Equals("Set-StrictMode", StringComparison.OrdinalIgnoreCase))));
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{path}: PowerShell returned invalid AST JSON: {ex.Message}", ex);
        }
    }

    private static PowerShellParser.PowerShellParseResult Fallback(string text, string reason)
    {
        ProviderWarnings.ReportOnce("powershell", "parser",
            $"{reason}; falling back to simpler PowerShell analysis (nested/dynamic syntax may be incomplete).");
        return PowerShellParser.Parse(text);
    }
}
