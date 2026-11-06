// Runs mypy and converts Python type diagnostics into provider violations.

using System.Text.Json;
using Cop.Core;

namespace Cop.Providers.Tools;

/// <summary>
/// Runs 'mypy . --output json --ignore-missing-imports' and exposes type-checking violations.
/// </summary>
public class PythonMypyProvider : ToolProvider
{
    protected override string ToolName => "mypy";

    protected override async ValueTask<List<object>> RunToolAsync(
        string rootPath, IReadOnlySet<string> excluded, CancellationToken cancellationToken)
    {
        (string? stdout, string _, int _) = await RunProcessAsync(
            new("mypy", ". --output json --ignore-missing-imports", rootPath)
            { Mode = ToolInvocationMode.Shell }, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(stdout))
        {
            return [];
        }

        List<object> violations = new List<object>();

        // mypy JSON output: one JSON object per line
        foreach (string line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.TrimStart().StartsWith('{'))
            {
                continue;
            }

            try
            {
                using JsonDocument doc = JsonDocument.Parse(line);
                JsonElement item = doc.RootElement;

                string filePath = NormalizePath(item.GetProperty("file").GetString() ?? string.Empty, rootPath);
                if (string.IsNullOrEmpty(filePath) || IsExcluded(filePath, excluded))
                {
                    continue;
                }

                int lineNum = item.TryGetProperty("line", out JsonElement ln) ? ln.GetInt32() : 0;
                string severity = item.TryGetProperty("severity", out JsonElement s) ? s.GetString() ?? "error" : "error";
                string message = item.TryGetProperty("message", out JsonElement m) ? m.GetString() ?? string.Empty : string.Empty;
                string? code = item.TryGetProperty("code", out JsonElement c) ? c.GetString() : null;

                violations.Add(new ToolViolation
                {
                    File = filePath,
                    Line = lineNum,
                    Severity = severity == "error" ? "error" : "warning",
                    Message = code != null ? $"{code}: {message}" : message,
                    Source = "mypy"
                });
            }
            catch (JsonException) { }
        }
        return violations;
    }
}
