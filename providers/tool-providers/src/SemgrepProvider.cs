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

    protected override ValueTask<List<object>> RunToolAsync(
        string rootPath, IReadOnlySet<string> excluded, CancellationToken cancellationToken)
        => RunJsonToolAsync(
            new("semgrep", "scan --json --quiet .", rootPath), rootPath, excluded, "results",
            item => item.TryGetProperty("path", out JsonElement p) ? p.GetString() ?? string.Empty : string.Empty,
            ToViolation, cancellationToken);

    private static ToolViolation ToViolation(JsonElement item, string filePath)
    {
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

        return new ToolViolation
        {
            File = filePath,
            Line = line,
            Severity = severity,
            Message = !string.IsNullOrEmpty(ruleId) ? $"{ruleId}: {message}" : message,
            Source = "semgrep"
        };
    }

    private static string MapSeverity(string? severity) => severity?.ToUpperInvariant() switch
    {
        "ERROR" => "error",
        "WARNING" => "warning",
        _ => "info"
    };
}
