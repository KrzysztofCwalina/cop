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
        var (stdout, _, _) = RunProcess("ruff", "check --output-format=json .", rootPath, shell: true);
        if (string.IsNullOrWhiteSpace(stdout)) return [];

        var violations = new List<object>();
        using var doc = JsonDocument.Parse(stdout);

        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var filePath = NormalizePath(item.GetProperty("filename").GetString() ?? "", rootPath);
            if (string.IsNullOrEmpty(filePath) || IsExcluded(filePath, excluded)) continue;

            var line = item.TryGetProperty("location", out var loc) && loc.TryGetProperty("row", out var row)
                ? row.GetInt32() : 0;
            var code = item.TryGetProperty("code", out var c) ? c.GetString() : "";
            var message = item.TryGetProperty("message", out var m) ? m.GetString() : "";

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
