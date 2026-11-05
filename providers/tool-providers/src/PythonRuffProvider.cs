// Runs Ruff and converts Python lint findings into provider violations.

using System.Text.Json;
using Cop.Core;

namespace Cop.Providers.Tools;

/// <summary>
/// Runs 'ruff check --output-format=json .' and exposes linting violations.
/// </summary>
public class PythonRuffProvider : ToolProvider
{
    protected override string ToolName => "ruff";

    protected override List<object> RunTool(string rootPath, IReadOnlySet<string> excluded)
    {
        (string? stdout, string _, int _) = RunProcess("ruff", "check --output-format=json .", rootPath, shell: true);
        if (string.IsNullOrWhiteSpace(stdout))
        {
            return [];
        }

        List<object> violations = new List<object>();
        using JsonDocument doc = JsonDocument.Parse(stdout);

        foreach (JsonElement item in doc.RootElement.EnumerateArray())
        {
            string filePath = NormalizePath(item.GetProperty("filename").GetString() ?? "", rootPath);
            if (string.IsNullOrEmpty(filePath) || IsExcluded(filePath, excluded))
            {
                continue;
            }

            int line = item.TryGetProperty("location", out JsonElement loc) && loc.TryGetProperty("row", out JsonElement row)
                ? row.GetInt32() : 0;
            string? code = item.TryGetProperty("code", out JsonElement c) ? c.GetString() : "";
            string? message = item.TryGetProperty("message", out JsonElement m) ? m.GetString() : "";

            violations.Add(new ToolViolation
            {
                File = filePath,
                Line = line,
                Severity = "warning",
                Message = $"{code}: {message}",
                Source = "ruff"
            });
        }
        return violations;
    }
}
