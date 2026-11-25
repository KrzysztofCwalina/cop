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

    protected override ValueTask<List<object>> RunToolAsync(
        string rootPath, IReadOnlySet<string> excluded, CancellationToken cancellationToken)
        => RunJsonToolAsync(
            new("bandit", "-r . -f json --quiet", rootPath) { Mode = ToolInvocationMode.Shell },
            rootPath, excluded, "results", item => item.GetProperty("filename").GetString() ?? string.Empty,
            ToViolation, cancellationToken);

    private static ToolViolation ToViolation(JsonElement item, string filePath)
    {
        int line = item.TryGetProperty("line_number", out JsonElement ln) ? ln.GetInt32() : 0;
        string? testId = item.TryGetProperty("test_id", out JsonElement tid) ? tid.GetString() : string.Empty;
        string? message = item.TryGetProperty("issue_text", out JsonElement m) ? m.GetString() : string.Empty;
        string severity = MapBanditSeverity(item.TryGetProperty("issue_severity", out JsonElement s) ? s.GetString() : string.Empty);

        return new ToolViolation
        {
            File = filePath,
            Line = line,
            Severity = severity,
            Message = $"{testId}: {message}",
            Source = "bandit"
        };
    }

    private static string MapBanditSeverity(string? severity) => severity?.ToUpperInvariant() switch
    {
        "HIGH" => "error",
        "MEDIUM" => "warning",
        _ => "info"
    };
}
