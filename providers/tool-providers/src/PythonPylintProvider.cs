using System.Text.Json;
using Cop.Core;

namespace Cop.Providers.Tools;

/// <summary>
/// Runs 'pylint --output-format=json --recursive=y .' and exposes linting violations.
/// </summary>
public class PythonPylintProvider : ToolProvider
{
    protected override string ToolName => "pylint";

    protected override List<object> RunTool(string rootPath, IReadOnlySet<string> excluded)
    {
        var (stdout, _, _) = RunProcess("pylint", "--output-format=json --recursive=y .", rootPath, shell: true);
        if (string.IsNullOrWhiteSpace(stdout)) return [];

        var violations = new List<object>();
        using var doc = JsonDocument.Parse(stdout);

        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var filePath = NormalizePath(item.GetProperty("path").GetString() ?? "", rootPath);
            if (string.IsNullOrEmpty(filePath) || IsExcluded(filePath, excluded)) continue;

            var line = item.TryGetProperty("line", out var ln) ? ln.GetInt32() : 0;
            var messageId = item.TryGetProperty("message-id", out var mid) ? mid.GetString() : "";
            var message = item.TryGetProperty("message", out var m) ? m.GetString() : "";
            var severity = MapPylintType(item.TryGetProperty("type", out var t) ? t.GetString() : "");

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
