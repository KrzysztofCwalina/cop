using System.Text.Json;
using Cop.Core;

namespace Cop.Providers.Tools;

/// <summary>
/// Runs 'checkov -d . -o json --quiet --compact' and exposes infrastructure-as-code violations.
/// </summary>
public class CheckovProvider : ToolProvider
{
    protected override string ToolName => "checkov";

    protected override List<object> RunTool(string rootPath, IReadOnlySet<string> excluded)
    {
        var (stdout, _, _) = RunProcess("checkov", "-d . -o json --quiet --compact", rootPath, shell: true);
        if (string.IsNullOrWhiteSpace(stdout)) return [];

        var violations = new List<object>();
        using var doc = JsonDocument.Parse(stdout);

        // checkov returns a dict for single check type, or a list for multiple
        var resultGroups = doc.RootElement.ValueKind == JsonValueKind.Array
            ? doc.RootElement.EnumerateArray().ToList()
            : [doc.RootElement];

        foreach (var group in resultGroups)
        {
            if (!group.TryGetProperty("results", out var results)) continue;
            if (!results.TryGetProperty("failed_checks", out var failedChecks)) continue;

            foreach (var item in failedChecks.EnumerateArray())
            {
                var filePath = NormalizePath(
                    item.TryGetProperty("file_path", out var fp) ? fp.GetString() ?? "" : "", rootPath);
                if (string.IsNullOrEmpty(filePath) || IsExcluded(filePath, excluded)) continue;

                var line = 0;
                if (item.TryGetProperty("file_line_range", out var lineRange) &&
                    lineRange.GetArrayLength() > 0)
                    line = lineRange[0].GetInt32();

                var ruleId = item.TryGetProperty("check_id", out var cid) ? cid.GetString() : "";
                var message = item.TryGetProperty("check_name", out var cn) ? cn.GetString() : "";

                violations.Add(new ToolViolation
                {
                    File = filePath,
                    Line = line,
                    Severity = "warning",
                    Message = !string.IsNullOrEmpty(ruleId) ? $"{ruleId}: {message}" : message ?? "",
                    Source = "checkov"
                });
            }
        }
        return violations;
    }
}
