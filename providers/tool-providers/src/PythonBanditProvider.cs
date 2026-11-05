// Runs Bandit and converts Python security findings into provider violations.

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
        (string? stdout, string _, int _) = RunProcess("bandit", "-r . -f json --quiet", rootPath, shell: true);
        if (string.IsNullOrWhiteSpace(stdout))
        {
            return [];
        }

        List<object> violations = new List<object>();
        using JsonDocument doc = JsonDocument.Parse(stdout);

        if (!doc.RootElement.TryGetProperty("results", out JsonElement results))
        {
            return [];
        }

        foreach (JsonElement item in results.EnumerateArray())
        {
            string filePath = NormalizePath(item.GetProperty("filename").GetString() ?? "", rootPath);
            if (string.IsNullOrEmpty(filePath) || IsExcluded(filePath, excluded))
            {
                continue;
            }

            int line = item.TryGetProperty("line_number", out JsonElement ln) ? ln.GetInt32() : 0;
            string? testId = item.TryGetProperty("test_id", out JsonElement tid) ? tid.GetString() : "";
            string? message = item.TryGetProperty("issue_text", out JsonElement m) ? m.GetString() : "";
            string severity = MapBanditSeverity(item.TryGetProperty("issue_severity", out JsonElement s) ? s.GetString() : "");

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
