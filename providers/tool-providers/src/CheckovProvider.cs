// Runs Checkov and converts infrastructure findings into provider violations.

using System.Text.Json;
using Cop.Core;

namespace Cop.Providers.Tools;

/// <summary>
/// Runs 'checkov -d . -o json --quiet --compact' and exposes infrastructure-as-code violations.
/// </summary>
public class CheckovProvider : ToolProvider
{
    protected override string ToolName => "checkov";

    protected override async ValueTask<List<object>> RunToolAsync(
        string rootPath, IReadOnlySet<string> excluded, CancellationToken cancellationToken)
    {
        (string? stdout, string _, int _) = await RunProcessAsync(
            new("checkov", "-d . -o json --quiet --compact", rootPath)
            { Mode = ToolInvocationMode.Shell }, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(stdout))
        {
            return [];
        }

        List<object> violations = new List<object>();
        using JsonDocument doc = JsonDocument.Parse(stdout);

        // checkov returns a dict for single check type, or a list for multiple
        List<JsonElement> resultGroups = doc.RootElement.ValueKind == JsonValueKind.Array
            ? doc.RootElement.EnumerateArray().ToList()
            : [doc.RootElement];

        foreach (JsonElement group in resultGroups)
        {
            if (!group.TryGetProperty("results", out JsonElement results))
            {
                continue;
            }

            if (!results.TryGetProperty("failed_checks", out JsonElement failedChecks))
            {
                continue;
            }

            foreach (JsonElement item in failedChecks.EnumerateArray())
            {
                string filePath = NormalizePath(
                    item.TryGetProperty("file_path", out JsonElement fp) ? fp.GetString() ?? string.Empty : string.Empty, rootPath);
                if (string.IsNullOrEmpty(filePath) || IsExcluded(filePath, excluded))
                {
                    continue;
                }

                int line = 0;
                if (item.TryGetProperty("file_line_range", out JsonElement lineRange) &&
                    lineRange.GetArrayLength() > 0)
                {
                    line = lineRange[0].GetInt32();
                }

                string? ruleId = item.TryGetProperty("check_id", out JsonElement cid) ? cid.GetString() : string.Empty;
                string? message = item.TryGetProperty("check_name", out JsonElement cn) ? cn.GetString() : string.Empty;

                violations.Add(new ToolViolation
                {
                    File = filePath,
                    Line = line,
                    Severity = "warning",
                    Message = !string.IsNullOrEmpty(ruleId) ? $"{ruleId}: {message}" : message ?? string.Empty,
                    Source = "checkov"
                });
            }
        }
        return violations;
    }
}
