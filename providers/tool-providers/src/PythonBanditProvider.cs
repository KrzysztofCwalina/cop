using System.Text.Json;
using Cop.Core;

namespace Cop.Providers.Tools;

/// <summary>
/// Runs 'bandit -r . -f json --quiet' and exposes security violations.
/// </summary>
public class PythonBanditProvider : ToolProvider
{
    protected override string ToolName => "bandit";

    protected override List<object> RunTool(string rootPath, IReadOnlySet<string> excluded)
    {
        var (stdout, _, _) = RunProcess("bandit", "-r . -f json --quiet", rootPath, shell: true);
        if (string.IsNullOrWhiteSpace(stdout)) return [];

        var violations = new List<object>();
        using var doc = JsonDocument.Parse(stdout);

        if (!doc.RootElement.TryGetProperty("results", out var results)) return [];

        foreach (var item in results.EnumerateArray())
        {
            var filePath = NormalizePath(item.GetProperty("filename").GetString() ?? "", rootPath);
            if (string.IsNullOrEmpty(filePath) || IsExcluded(filePath, excluded)) continue;

            var line = item.TryGetProperty("line_number", out var ln) ? ln.GetInt32() : 0;
            var testId = item.TryGetProperty("test_id", out var tid) ? tid.GetString() : "";
            var message = item.TryGetProperty("issue_text", out var m) ? m.GetString() : "";
            var severity = MapBanditSeverity(item.TryGetProperty("issue_severity", out var s) ? s.GetString() : "");

            violations.Add(new ToolViolation
            {
                File = filePath,
                Line = line,
                Severity = severity,
                Message = $"{testId}: {message}",
                Source = "bandit"
            });
        }
        return violations;
    }

    private static string MapBanditSeverity(string? severity) => severity?.ToUpperInvariant() switch
    {
        "HIGH" => "error",
        "MEDIUM" => "warning",
        _ => "info"
    };
}
