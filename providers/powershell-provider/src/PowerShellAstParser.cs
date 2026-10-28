using System.Text;
using System.Text.Json;
using Cop.Core;

namespace Cop.Providers.PowerShell;

internal static class PowerShellAstParser
{
    private const string Script = """
        $ErrorActionPreference = 'Stop'
        [Console]::InputEncoding = [System.Text.UTF8Encoding]::new($false)
        [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
        while ($null -ne ($request = [Console]::In.ReadLine())) {
        $source = ($request | ConvertFrom-Json).Source
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

    internal static PowerShellParser.PowerShellParseResult Parse(string text, string path)
    {
        var executable = ToolchainProcess.FindExecutable("pwsh") ?? ToolchainProcess.FindExecutable("powershell");
        if (executable is null)
            return Fallback(text, "PowerShell is not installed on PATH");
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(Script));
        var result = ToolchainExecutionScope.RequestAsync(executable,
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", encoded],
            Path.GetTempPath(), JsonSerializer.Serialize(new { Source = text }), timeout: TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
        if (result.Status != ToolchainStatus.Success)
            return Fallback(text, result.Diagnostic ?? result.StandardError);

        try
        {
            using var document = JsonDocument.Parse(result.StandardOutput);
            foreach (var error in document.RootElement.GetProperty("Errors").EnumerateArray())
                ProviderErrors.Report($"{path}: {error.GetString()}");
            var commands = document.RootElement.GetProperty("Commands").EnumerateArray().Select(c =>
                new PowerShellCommandInfo(c.GetProperty("Name").GetString()!,
                    c.GetProperty("Text").GetString()!, c.GetProperty("Line").GetInt32())).ToList();
            return new(commands, new(commands.Any(c => c.Name.Equals("Set-StrictMode", StringComparison.OrdinalIgnoreCase))));
        }
        catch (JsonException ex)
        {
            return Fallback(text, $"PowerShell returned invalid AST JSON: {ex.Message}");
        }
    }

    private static PowerShellParser.PowerShellParseResult Fallback(string text, string reason)
    {
        ProviderWarnings.ReportOnce("powershell", "parser",
            $"{reason}; falling back to simpler PowerShell analysis (nested/dynamic syntax may be incomplete).");
        return PowerShellParser.Parse(text);
    }
}
