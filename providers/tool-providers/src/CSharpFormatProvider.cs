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

    protected override async ValueTask<List<object>> RunToolAsync(
        string rootPath, IReadOnlySet<string> excluded, CancellationToken cancellationToken)
    {
        string reportDir = Path.Combine(Path.GetTempPath(), $"cop-format-{Guid.NewGuid():N}");
        Directory.CreateDirectory(reportDir);

        try
        {
            await RunProcessAsync(
                new("dotnet", $"format --verify-no-changes --report \"{reportDir}\"", rootPath),
                cancellationToken).ConfigureAwait(false);

            List<object> violations = new List<object>();
            foreach (string jsonFile in Directory.GetFiles(reportDir, "*.json", SearchOption.AllDirectories))
            {
                string json = await File.ReadAllTextAsync(jsonFile, cancellationToken)
                    .ConfigureAwait(false);
                List<JsonElement> entries = ParseReportEntries(json);

                foreach (JsonElement entry in entries)
                {
                    string filePath = NormalizePath(
                        entry.GetProperty("FilePath").GetString() ?? entry.GetProperty("FileName").GetString() ?? string.Empty,
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
            try
            {
                Directory.Delete(reportDir, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ProviderWarnings.ReportOnce("dotnet-format", reportDir,
                    $"Cannot remove formatting report directory '{reportDir}': {ex.Message}");
            }
        }
    }

    private static List<JsonElement> ParseReportEntries(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        if (root.ValueKind == JsonValueKind.Array)
        {
            return root.EnumerateArray().Select(entry => entry.Clone()).ToList();
        }

        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("Files", out JsonElement files) || root.TryGetProperty("files", out files))
            {
                if (files.ValueKind == JsonValueKind.Array)
                {
                    return files.EnumerateArray().Select(entry => entry.Clone()).ToList();
                }
            }
        }

        throw new InvalidDataException(
            "The dotnet format report must be an array or an object containing a Files array.");
    }
}
