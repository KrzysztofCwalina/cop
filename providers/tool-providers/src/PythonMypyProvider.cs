using System.Text.Json;
using Cop.Core;

namespace Cop.Providers.Tools;

/// <summary>
/// Runs 'mypy . --output json --ignore-missing-imports' and exposes type-checking violations.
/// </summary>
public class PythonMypyProvider : ToolProvider
{
    protected override string ToolName => "mypy";

    protected override List<object> RunTool(string rootPath, IReadOnlySet<string> excluded)
    {
        var (stdout, _, _) = RunProcess("mypy", ". --output json --ignore-missing-imports", rootPath, shell: true);
        if (string.IsNullOrWhiteSpace(stdout)) return [];

        var violations = new List<object>();

        // mypy JSON output: one JSON object per line
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.TrimStart().StartsWith('{')) continue;

            try
            {
                using var doc = JsonDocument.Parse(line);
                var item = doc.RootElement;

                var filePath = NormalizePath(item.GetProperty("file").GetString() ?? "", rootPath);
                if (string.IsNullOrEmpty(filePath) || IsExcluded(filePath, excluded)) continue;

                var lineNum = item.TryGetProperty("line", out var ln) ? ln.GetInt32() : 0;
                var severity = item.TryGetProperty("severity", out var s) ? s.GetString() ?? "error" : "error";
                var message = item.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
                var code = item.TryGetProperty("code", out var c) ? c.GetString() : null;

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
