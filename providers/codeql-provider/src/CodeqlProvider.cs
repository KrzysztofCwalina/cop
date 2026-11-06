// Runs CodeQL analysis and projects its results into provider violations.

using System.Text.Json;
using Cop.Core;
using Cop.Lang;

namespace Cop.Providers;

/// <summary>
/// Provider that reads CodeQL SARIF analysis results and exposes them as cop collections.
/// Supports loading SARIF files (the standard output format of `codeql database analyze`).
/// Import with: import codeql
/// </summary>
public class CodeqlProvider : DataProvider
{

    /// <inheritdoc />
    public override ReadOnlyMemory<byte> GetSchema()
    {
        ProviderSchema schema = new ProviderSchema
        {
            Collections =
            [
                new ProviderCollectionSchema { Name = "Violations", ItemType = "Violation" },
                new ProviderCollectionSchema { Name = "Rules", ItemType = "Rule" }
            ],
            Types =
            [
                new ProviderTypeSchema
                {
                    Name = "Violation",
                    Properties =
                    [
                        new ProviderPropertySchema { Name = "File" },
                        new ProviderPropertySchema { Name = "Line", Type = "int" },
                        new ProviderPropertySchema { Name = "Severity" },
                        new ProviderPropertySchema { Name = "Message" },
                        new ProviderPropertySchema { Name = "Source" }
                    ]
                },
                new ProviderTypeSchema
                {
                    Name = "Rule",
                    Properties =
                    [
                        new ProviderPropertySchema { Name = "Id" },
                        new ProviderPropertySchema { Name = "Name" },
                        new ProviderPropertySchema { Name = "Description" },
                        new ProviderPropertySchema { Name = "Severity" },
                        new ProviderPropertySchema { Name = "Tags" },
                        new ProviderPropertySchema { Name = "Precision" }
                    ]
                }
            ]
        };
        return schema.ToJson();
    }

    /// <inheritdoc />
    public override async ValueTask<object?> QueryAsync(
        ProviderQuery query, CancellationToken cancellationToken = default)
    {
        // Auto-discover SARIF files in the root path
        List<object> violations = new List<object>();
        List<object> rules = new List<object>();

        if (!string.IsNullOrEmpty(query.RootPath) && Directory.Exists(query.RootPath))
        {
            string[] sarifFiles = Directory.GetFiles(query.RootPath, "*.sarif", SearchOption.TopDirectoryOnly);
            foreach (string file in sarifFiles)
            {
                await LoadSarifFileAsync(file, violations, rules, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        return new Dictionary<string, List<object>>
        {
            ["Violations"] = violations,
            ["Rules"] = rules
        };
    }

    /// <inheritdoc />
    public override RuntimeBindings? GetRuntimeBindings() => new()
    {
        ClrTypeMappings = new Dictionary<Type, string>
        {
            [typeof(CodeqlViolation)] = "Violation",
            [typeof(CodeqlRule)] = "Rule"
        },
        Accessors = new Dictionary<string, Dictionary<string, Func<object, object?>>>
        {
            ["Violation"] = new()
            {
                ["File"] = o => ((CodeqlViolation)o).File,
                ["Line"] = o => ((CodeqlViolation)o).Line,
                ["Severity"] = o => ((CodeqlViolation)o).Severity,
                ["Message"] = o => ((CodeqlViolation)o).Message,
                ["Source"] = o => ((CodeqlViolation)o).Source
            },
            ["Rule"] = new()
            {
                ["Id"] = o => ((CodeqlRule)o).Id,
                ["Name"] = o => ((CodeqlRule)o).Name,
                ["Description"] = o => ((CodeqlRule)o).Description,
                ["Severity"] = o => ((CodeqlRule)o).Severity,
                ["Tags"] = o => ((CodeqlRule)o).Tags,
                ["Precision"] = o => ((CodeqlRule)o).Precision
            }
        }
    };

    private static async ValueTask LoadSarifFileAsync(
        string filePath, List<object> violations, List<object> rules,
        CancellationToken cancellationToken)
    {
        try
        {
            byte[] json = await File.ReadAllBytesAsync(filePath, cancellationToken)
                .ConfigureAwait(false);
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            if (!root.TryGetProperty("runs", out JsonElement runs))
            {
                return;
            }

            foreach (JsonElement run in runs.EnumerateArray())
            {
                // Extract rules from the tool driver
                Dictionary<string, (string Severity, string Name, string Description, string Tags, string Precision)> ruleMap = new Dictionary<string, (string Severity, string Name, string Description, string Tags, string Precision)>();
                if (run.TryGetProperty("tool", out JsonElement tool) &&
                    tool.TryGetProperty("driver", out JsonElement driver) &&
                    driver.TryGetProperty("rules", out JsonElement rulesArray))
                {
                    foreach (JsonElement rule in rulesArray.EnumerateArray())
                    {
                        string id = rule.GetProperty("id").GetString() ?? string.Empty;
                        string name = rule.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? string.Empty : string.Empty;
                        string desc = string.Empty;
                        if (rule.TryGetProperty("shortDescription", out JsonElement sd) && sd.TryGetProperty("text", out JsonElement sdt))
                        {
                            desc = sdt.GetString() ?? string.Empty;
                        }
                        else if (rule.TryGetProperty("fullDescription", out JsonElement fd) && fd.TryGetProperty("text", out JsonElement fdt))
                        {
                            desc = fdt.GetString() ?? string.Empty;
                        }

                        string severity = "warning";
                        string precision = string.Empty;
                        if (rule.TryGetProperty("properties", out JsonElement props))
                        {
                            if (props.TryGetProperty("problem.severity", out JsonElement sev))
                            {
                                severity = sev.GetString() ?? "warning";
                            }

                            if (props.TryGetProperty("precision", out JsonElement prec))
                            {
                                precision = prec.GetString() ?? string.Empty;
                            }
                        }

                        string tags = string.Empty;
                        if (rule.TryGetProperty("properties", out JsonElement p2) && p2.TryGetProperty("tags", out JsonElement tagsArr))
                        {
                            List<string> tagList = new List<string>();
                            foreach (JsonElement t in tagsArr.EnumerateArray())
                            {
                                tagList.Add(t.GetString() ?? string.Empty);
                            }

                            tags = string.Join(", ", tagList);
                        }

                        ruleMap[id] = (severity, name, desc, tags, precision);
                        rules.Add(new CodeqlRule(id, name, desc, severity, tags, precision));
                    }
                }

                // Extract results
                if (!run.TryGetProperty("results", out JsonElement resultsArray))
                {
                    continue;
                }

                foreach (JsonElement result in resultsArray.EnumerateArray())
                {
                    string ruleId = result.TryGetProperty("ruleId", out JsonElement rid) ? rid.GetString() ?? string.Empty : string.Empty;
                    string message = string.Empty;
                    if (result.TryGetProperty("message", out JsonElement msg) && msg.TryGetProperty("text", out JsonElement msgText))
                    {
                        message = msgText.GetString() ?? string.Empty;
                    }

                    string severity = "warning";
                    if (ruleMap.TryGetValue(ruleId, out (string Severity, string Name, string Description, string Tags, string Precision) ruleInfo))
                    {
                        severity = ruleInfo.Severity;
                    }

                    if (result.TryGetProperty("level", out JsonElement lvl))
                    {
                        severity = lvl.GetString() switch
                        {
                            "error" => "error",
                            "warning" => "warning",
                            "note" => "info",
                            "none" => "info",
                            _ => severity
                        };
                    }

                    // Map note/recommendation to info for Violation compatibility
                    if (severity is "note" or "recommendation")
                    {
                        severity = "info";
                    }

                    string file = string.Empty;
                    int startLine = 0;

                    if (result.TryGetProperty("locations", out JsonElement locs))
                    {
                        foreach (JsonElement loc in locs.EnumerateArray())
                        {
                            if (loc.TryGetProperty("physicalLocation", out JsonElement physLoc))
                            {
                                if (physLoc.TryGetProperty("artifactLocation", out JsonElement artLoc) &&
                                    artLoc.TryGetProperty("uri", out JsonElement uri))
                                {
                                    file = uri.GetString() ?? string.Empty;
                                }

                                if (physLoc.TryGetProperty("region", out JsonElement region))
                                {
                                    startLine = region.TryGetProperty("startLine", out JsonElement sl) ? sl.GetInt32() : 0;
                                }
                            }
                            break; // only use first location
                        }
                    }

                    // Combine RuleId into Message
                    string fullMessage = string.IsNullOrEmpty(ruleId) ? message : $"{ruleId}: {message}";
                    violations.Add(new CodeqlViolation(file, startLine, severity, fullMessage, "codeql"));
                }
            }
        }
        catch (JsonException)
        {
            // Skip malformed SARIF files
        }
    }

    /// <inheritdoc />
    public override string ToString() => "CodeqlProvider";
}
