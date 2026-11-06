// Runs Pylint and converts Python diagnostics into provider violations.

using System.Text.Json;
using Cop.Core;

namespace Cop.Providers.Tools;

/// <summary>
/// Runs 'pylint --output-format=json --recursive=y .' and exposes linting violations.
/// </summary>
public class PythonPylintProvider : ToolProvider
{
    protected override string ToolName => "pylint";

    protected override async ValueTask<List<object>> RunToolAsync(
        string rootPath, IReadOnlySet<string> excluded, CancellationToken cancellationToken)
    {
        (string? stdout, string _, int _) = await RunProcessAsync(
            new("pylint", "--output-format=json --recursive=y .", rootPath)
            { Mode = ToolInvocationMode.Shell }, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(stdout))
        {
            return [];
        }

        List<object> violations = new List<object>();
        using JsonDocument doc = JsonDocument.Parse(stdout);

        foreach (JsonElement item in doc.RootElement.EnumerateArray())
        {
            string filePath = NormalizePath(item.GetProperty("path").GetString() ?? string.Empty, rootPath);
            if (string.IsNullOrEmpty(filePath) || IsExcluded(filePath, excluded))
            {
                continue;
            }

            int line = item.TryGetProperty("line", out JsonElement ln) ? ln.GetInt32() : 0;
            string? messageId = item.TryGetProperty("message-id", out JsonElement mid) ? mid.GetString() : string.Empty;
            string? message = item.TryGetProperty("message", out JsonElement m) ? m.GetString() : string.Empty;
            string severity = MapPylintType(item.TryGetProperty("type", out JsonElement t) ? t.GetString() : string.Empty);

            violations.Add(new ToolViolation
            {
                File = filePath,
                Line = line,
                Severity = severity,
                Message = $"{messageId}: {message}",
                Source = "pylint"
            });
        }
        return violations;
    }

    private static string MapPylintType(string? type) => type switch
    {
        "error" or "fatal" => "error",
        "warning" => "warning",
        _ => "info"
    };
}
