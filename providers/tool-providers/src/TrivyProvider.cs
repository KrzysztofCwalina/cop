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
        var (stdout, _, _) = RunProcess("trivy", "fs --format json --scanners vuln,misconfig .", rootPath);
        if (string.IsNullOrWhiteSpace(stdout)) return [];

        var violations = new List<object>();
        using var doc = JsonDocument.Parse(stdout);

        if (!doc.RootElement.TryGetProperty("Results", out var results)) return [];

        foreach (var result in results.EnumerateArray())
        {
            var filePath = NormalizePath(
                result.TryGetProperty("Target", out var target) ? target.GetString() ?? "" : "", rootPath);
            if (string.IsNullOrEmpty(filePath) || IsExcluded(filePath, excluded)) continue;

            // Vulnerabilities
            if (result.TryGetProperty("Vulnerabilities", out var vulns))
            {
                foreach (var vuln in vulns.EnumerateArray())
                {
                    var ruleId = vuln.TryGetProperty("VulnerabilityID", out var vid) ? vid.GetString() : "";
                    var message = BuildVulnMessage(vuln);
                    var severity = MapSeverity(vuln.TryGetProperty("Severity", out var s) ? s.GetString() : "");

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
            if (result.TryGetProperty("Misconfigurations", out var misconfigs))
            {
                foreach (var mc in misconfigs.EnumerateArray())
                {
                    var line = 0;
                    if (mc.TryGetProperty("CauseMetadata", out var meta) &&
                        meta.TryGetProperty("StartLine", out var sl))
                        line = sl.GetInt32();

                    var ruleId = mc.TryGetProperty("ID", out var id) ? id.GetString() :
                        mc.TryGetProperty("AVDID", out var avd) ? avd.GetString() : "";
                    var message = mc.TryGetProperty("Title", out var t) ? t.GetString() :
                        mc.TryGetProperty("Message", out var m) ? m.GetString() : "";
                    var severity = MapSeverity(mc.TryGetProperty("Severity", out var s) ? s.GetString() : "");

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
        var title = vuln.TryGetProperty("Title", out var t) ? t.GetString() :
            vuln.TryGetProperty("Description", out var d) ? d.GetString() :
            vuln.TryGetProperty("VulnerabilityID", out var v) ? v.GetString() : "Vulnerability";
        var pkg = vuln.TryGetProperty("PkgName", out var p) ? p.GetString() : "unknown-package";
        var ver = vuln.TryGetProperty("InstalledVersion", out var iv) ? iv.GetString() : "unknown-version";
        return $"{title} in {pkg}@{ver}";
    }

    private static string MapSeverity(string? severity) => severity?.ToUpperInvariant() switch
    {
        "CRITICAL" or "HIGH" => "error",
        "MEDIUM" => "warning",
        _ => "info"
    };
}
