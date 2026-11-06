// Runs Semgrep and converts matched rules into provider violations.

using System.Text.Json;
using Cop.Core;

namespace Cop.Providers.Tools;

/// <summary>
/// Runs 'semgrep scan --json --quiet .' and exposes findings as violations.
/// </summary>
public class SemgrepProvider : ToolProvider
{
    protected override string ToolName => "semgrep";

    protected override async ValueTask<List<object>> RunToolAsync(
        string rootPath, IReadOnlySet<string> excluded, CancellationToken cancellationToken)
    {
        (string? stdout, string _, int _) = await RunProcessAsync(
            new("semgrep", "scan --json --quiet .", rootPath), cancellationToken)
            .ConfigureAwait(false);
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
            string filePath = NormalizePath(
                item.TryGetProperty("path", out JsonElement p) ? p.GetString() ?? string.Empty : string.Empty, rootPath);
            if (string.IsNullOrEmpty(filePath) || IsExcluded(filePath, excluded))
            {
                continue;
            }

            int line = 0;
            if (item.TryGetProperty("start", out JsonElement start) && start.TryGetProperty("line", out JsonElement ln))
            {
                line = ln.GetInt32();
            }

            string? ruleId = item.TryGetProperty("check_id", out JsonElement cid) ? cid.GetString() : string.Empty;
            string message = string.Empty;
            if (item.TryGetProperty("extra", out JsonElement extra))
            {
                message = extra.TryGetProperty("message", out JsonElement m) ? m.GetString() ?? string.Empty : string.Empty;
            }

            string severity = "warning";
            if (item.TryGetProperty("extra", out JsonElement ex2) && ex2.TryGetProperty("severity", out JsonElement s))
            {
                severity = MapSeverity(s.GetString());
            }

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
