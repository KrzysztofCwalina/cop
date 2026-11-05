// Runs dotnet format and converts formatting findings into provider violations.

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
        string reportDir = Path.Combine(Path.GetTempPath(), $"cop-format-{Guid.NewGuid():N}");
        Directory.CreateDirectory(reportDir);

        try
        {
            RunProcess("dotnet", $"format --verify-no-changes --report \"{reportDir}\"", rootPath);

            List<object> violations = new List<object>();
            foreach (string jsonFile in Directory.GetFiles(reportDir, "*.json", SearchOption.AllDirectories))
            {
                string json = File.ReadAllText(jsonFile);
                List<JsonElement> entries = ParseReportEntries(json);

                foreach (JsonElement entry in entries)
                {
                    string filePath = NormalizePath(
                        entry.GetProperty("FilePath").GetString() ?? entry.GetProperty("FileName").GetString() ?? "",
                        rootPath);
                    if (string.IsNullOrEmpty(filePath) || IsExcluded(filePath, excluded))
                    {
                        continue;
                    }

                    if (entry.TryGetProperty("FileChanges", out JsonElement changes))
                    {
                        foreach (JsonElement change in changes.EnumerateArray())
                        {
                            int line = change.TryGetProperty("LineNumber", out JsonElement ln) ? ln.GetInt32() : 0;
                            string? ruleId = change.TryGetProperty("DiagnosticId", out JsonElement did) ? did.GetString() : "IDE0055";
                            string? message = change.TryGetProperty("FormatDescription", out JsonElement desc) ? desc.GetString() : "Formatting violation";
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
        JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        if (root.ValueKind == JsonValueKind.Array)
        {
            return [.. root.EnumerateArray()];
        }

        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("Files", out JsonElement files) || root.TryGetProperty("files", out files))
            {
                return [.. files.EnumerateArray()];
            }
        }

        return [];
    }
}
