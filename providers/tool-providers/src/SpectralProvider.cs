using System.Text.Json;
using Cop.Core;

namespace Cop.Providers.Tools;

/// <summary>
/// Runs 'spectral lint --format json' on API specs and exposes linting violations.
/// Falls back to 'npx @stoplight/spectral-cli' if spectral is not installed globally.
/// </summary>
public class SpectralProvider : ToolProvider
{
    protected override string ToolName => "spectral";

    protected override List<object> RunTool(string rootPath, IReadOnlySet<string> excluded)
    {
        // Create temporary .spectral.json if none exists
        var configPath = Path.Combine(rootPath, ".spectral.json");
        bool createdConfig = false;
        if (!File.Exists(configPath) &&
            !File.Exists(Path.Combine(rootPath, ".spectral.yaml")) &&
            !File.Exists(Path.Combine(rootPath, ".spectral.yml")))
        {
            File.WriteAllText(configPath, """{"extends":["spectral:oas"]}""");
            createdConfig = true;
        }

        try
        {
            var stdout = TryRunSpectral(rootPath);
            if (stdout == null) return [];
            if (string.IsNullOrWhiteSpace(stdout)) return [];

            var violations = new List<object>();
            using var doc = JsonDocument.Parse(stdout);

            if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var filePath = NormalizePath(
                    item.TryGetProperty("source", out var s) ? s.GetString() ?? "" : "", rootPath);
                if (string.IsNullOrEmpty(filePath) || IsExcluded(filePath, excluded)) continue;

                var line = 0;
                if (item.TryGetProperty("range", out var range) &&
                    range.TryGetProperty("start", out var start) &&
                    start.TryGetProperty("line", out var ln))
                    line = ln.GetInt32() + 1; // Spectral uses 0-based lines

                var ruleId = item.TryGetProperty("code", out var code) ? code.GetString() : "";
                var message = item.TryGetProperty("message", out var m) ? m.GetString() : "";
                var severity = MapSeverity(item.TryGetProperty("severity", out var sev) ? sev.GetInt32() : 2);

                violations.Add(new ToolViolation
                {
                    File = filePath,
                    Line = line,
                    Severity = severity,
                    Message = !string.IsNullOrEmpty(ruleId) ? $"{ruleId}: {message}" : message ?? "",
                    Source = "spectral"
                });
            }
            return violations;
        }
        finally
        {
            if (createdConfig) try { File.Delete(configPath); } catch { }
        }
    }

    private static string? TryRunSpectral(string rootPath)
    {
        var isWindows = OperatingSystem.IsWindows();
        var attempts = isWindows
            ? new[] { ("spectral.cmd", "lint --format json **/*.{json,yaml,yml}"),
                      ("npx.cmd", "@stoplight/spectral-cli lint --format json **/*.{json,yaml,yml}") }
            : new[] { ("spectral", "lint --format json **/*.{json,yaml,yml}"),
                      ("npx", "@stoplight/spectral-cli lint --format json **/*.{json,yaml,yml}") };

        foreach (var (cmd, args) in attempts)
        {
            try
            {
                var (stdout, _, _) = RunProcess(cmd, args, rootPath, shell: isWindows);
                return stdout;
            }
            catch (Exception ex) when (ex.InnerException is System.ComponentModel.Win32Exception ||
                                       ex is InvalidOperationException)
            {
                continue;
            }
        }

        Console.Error.WriteLine("Spectral not found. Install with: npm install -g @stoplight/spectral-cli");
        return null;
    }

    private static string MapSeverity(int severity) => severity switch
    {
        0 => "error",
        1 => "warning",
        _ => "info"
    };
}
