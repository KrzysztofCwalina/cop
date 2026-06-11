using System.Text.Json;
using Cop.Core;

namespace Cop.Providers.Tools;

/// <summary>
/// Runs 'semgrep scan --json --quiet .' and exposes findings as violations.
/// </summary>
public class SemgrepProvider : ToolProvider
{
    protected override string ToolName => "semgrep";

    protected override List<object> RunTool(string rootPath, IReadOnlySet<string> excluded)
    {
        var (stdout, _, _) = RunProcess("semgrep", "scan --json --quiet .", rootPath);
        if (string.IsNullOrWhiteSpace(stdout)) return [];

        var violations = new List<object>();
        using var doc = JsonDocument.Parse(stdout);

        if (!doc.RootElement.TryGetProperty("results", out var results)) return [];

        foreach (var item in results.EnumerateArray())
        {
            var filePath = NormalizePath(
                item.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "", rootPath);
            if (string.IsNullOrEmpty(filePath) || IsExcluded(filePath, excluded)) continue;

            var line = 0;
            if (item.TryGetProperty("start", out var start) && start.TryGetProperty("line", out var ln))
                line = ln.GetInt32();

            var ruleId = item.TryGetProperty("check_id", out var cid) ? cid.GetString() : "";
            var message = "";
            if (item.TryGetProperty("extra", out var extra))
                message = extra.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";

            var severity = "warning";
            if (item.TryGetProperty("extra", out var ex2) && ex2.TryGetProperty("severity", out var s))
                severity = MapSeverity(s.GetString());

            violations.Add(new ToolViolation
            {
                File = filePath,
                Line = line,
                Severity = severity,
                Message = !string.IsNullOrEmpty(ruleId) ? $"{ruleId}: {message}" : message,
                Source = "semgrep"
            });
        }
        return violations;
    }

    private static string MapSeverity(string? severity) => severity?.ToUpperInvariant() switch
    {
        "ERROR" => "error",
        "WARNING" => "warning",
        _ => "info"
    };
}
