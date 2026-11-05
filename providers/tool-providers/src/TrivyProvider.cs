// Runs Trivy and converts vulnerability or configuration findings into violations.

using System.Text.Json;
using Cop.Core;

namespace Cop.Providers.Tools;

/// <summary>
/// Runs 'trivy fs --format json --scanners vuln,misconfig .' and exposes vulnerabilities and misconfigurations.
/// </summary>
public class TrivyProvider : ToolProvider
{
    protected override string ToolName => "trivy";

    protected override List<object> RunTool(string rootPath, IReadOnlySet<string> excluded)
    {
        (string? stdout, string _, int _) = RunProcess("trivy", "fs --format json --scanners vuln,misconfig .", rootPath);
        if (string.IsNullOrWhiteSpace(stdout))
        {
            return [];
        }

        List<object> violations = new List<object>();
        using JsonDocument doc = JsonDocument.Parse(stdout);

        if (!doc.RootElement.TryGetProperty("Results", out JsonElement results))
        {
            return [];
        }

        foreach (JsonElement result in results.EnumerateArray())
        {
            string filePath = NormalizePath(
                result.TryGetProperty("Target", out JsonElement target) ? target.GetString() ?? "" : "", rootPath);
            if (string.IsNullOrEmpty(filePath) || IsExcluded(filePath, excluded))
            {
                continue;
            }

            // Vulnerabilities
            if (result.TryGetProperty("Vulnerabilities", out JsonElement vulns))
            {
                foreach (JsonElement vuln in vulns.EnumerateArray())
                {
                    string? ruleId = vuln.TryGetProperty("VulnerabilityID", out JsonElement vid) ? vid.GetString() : "";
                    string message = BuildVulnMessage(vuln);
                    string severity = MapSeverity(vuln.TryGetProperty("Severity", out JsonElement s) ? s.GetString() : "");

                    violations.Add(new ToolViolation
                    {
                        File = filePath,
                        Line = 0,
                        Severity = severity,
                        Message = !string.IsNullOrEmpty(ruleId) ? $"{ruleId}: {message}" : message,
                        Source = "trivy"
                    });
                }
            }

            // Misconfigurations
            if (result.TryGetProperty("Misconfigurations", out JsonElement misconfigs))
            {
                foreach (JsonElement mc in misconfigs.EnumerateArray())
                {
                    int line = 0;
                    if (mc.TryGetProperty("CauseMetadata", out JsonElement meta) &&
                        meta.TryGetProperty("StartLine", out JsonElement sl))
                    {
                        line = sl.GetInt32();
                    }

                    string? ruleId = mc.TryGetProperty("ID", out JsonElement id) ? id.GetString() :
                        mc.TryGetProperty("AVDID", out JsonElement avd) ? avd.GetString() : "";
                    string? message = mc.TryGetProperty("Title", out JsonElement t) ? t.GetString() :
                        mc.TryGetProperty("Message", out JsonElement m) ? m.GetString() : "";
                    string severity = MapSeverity(mc.TryGetProperty("Severity", out JsonElement s) ? s.GetString() : "");

                    violations.Add(new ToolViolation
                    {
                        File = filePath,
                        Line = line,
                        Severity = severity,
                        Message = !string.IsNullOrEmpty(ruleId) ? $"{ruleId}: {message}" : message ?? "",
                        Source = "trivy"
                    });
                }
            }
        }
        return violations;
    }

    private static string BuildVulnMessage(JsonElement vuln)
    {
        string? title = vuln.TryGetProperty("Title", out JsonElement t) ? t.GetString() :
            vuln.TryGetProperty("Description", out JsonElement d) ? d.GetString() :
            vuln.TryGetProperty("VulnerabilityID", out JsonElement v) ? v.GetString() : "Vulnerability";
        string? pkg = vuln.TryGetProperty("PkgName", out JsonElement p) ? p.GetString() : "unknown-package";
        string? ver = vuln.TryGetProperty("InstalledVersion", out JsonElement iv) ? iv.GetString() : "unknown-version";
        return $"{title} in {pkg}@{ver}";
    }

    private static string MapSeverity(string? severity) => severity?.ToUpperInvariant() switch
    {
        "CRITICAL" or "HIGH" => "error",
        "MEDIUM" => "warning",
        _ => "info"
    };
}
