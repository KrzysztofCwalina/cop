using System.Text.Json;
using Cop.Core;

namespace Cop.Providers.Tools;

/// <summary>
/// Runs 'dotnet format --verify-no-changes --report' and exposes formatting violations.
/// </summary>
public class CSharpFormatProvider : ToolProvider
{
    protected override string ToolName => "dotnet";

    protected override List<object> RunTool(string rootPath, IReadOnlySet<string> excluded)
    {
        var reportDir = Path.Combine(Path.GetTempPath(), $"cop-format-{Guid.NewGuid():N}");
        Directory.CreateDirectory(reportDir);

        try
        {
            RunProcess("dotnet", $"format --verify-no-changes --report \"{reportDir}\"", rootPath);

            var violations = new List<object>();
            foreach (var jsonFile in Directory.GetFiles(reportDir, "*.json", SearchOption.AllDirectories))
            {
                var json = File.ReadAllText(jsonFile);
                var entries = ParseReportEntries(json);

                foreach (var entry in entries)
                {
                    var filePath = NormalizePath(
                        entry.GetProperty("FilePath").GetString() ?? entry.GetProperty("FileName").GetString() ?? "",
                        rootPath);
                    if (string.IsNullOrEmpty(filePath) || IsExcluded(filePath, excluded))
                        continue;

                    if (entry.TryGetProperty("FileChanges", out var changes))
                    {
                        foreach (var change in changes.EnumerateArray())
                        {
                            var line = change.TryGetProperty("LineNumber", out var ln) ? ln.GetInt32() : 0;
                            var ruleId = change.TryGetProperty("DiagnosticId", out var did) ? did.GetString() : "IDE0055";
                            var message = change.TryGetProperty("FormatDescription", out var desc) ? desc.GetString() : "Formatting violation";
                            violations.Add(new ToolViolation
                            {
                                File = filePath,
                                Line = line,
                                Severity = "warning",
                                Message = $"{ruleId}: {message}",
                                Source = "dotnet-format"
                            });
                        }
                    }
                }
            }
            return violations;
        }
        finally
        {
            try { Directory.Delete(reportDir, true); } catch { }
        }
    }

    private static List<JsonElement> ParseReportEntries(string json)
    {
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.ValueKind == JsonValueKind.Array)
            return [.. root.EnumerateArray()];

        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("Files", out var files) || root.TryGetProperty("files", out files))
                return [.. files.EnumerateArray()];
        }

        return [];
    }
}
