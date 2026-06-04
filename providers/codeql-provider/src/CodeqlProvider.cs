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

    public override ReadOnlyMemory<byte> GetSchema()
    {
        var schema = new ProviderSchema
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

    public override object? Query(ProviderQuery query)
    {
        // Auto-discover SARIF files in the root path
        var violations = new List<object>();
        var rules = new List<object>();

        if (!string.IsNullOrEmpty(query.RootPath) && Directory.Exists(query.RootPath))
        {
            var sarifFiles = Directory.GetFiles(query.RootPath, "*.sarif", SearchOption.TopDirectoryOnly);
            foreach (var file in sarifFiles)
            {
                LoadSarifFile(file, violations, rules);
            }
        }

        return new Dictionary<string, List<object>>
        {
            ["Violations"] = violations,
            ["Rules"] = rules
        };
    }

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

    private static void LoadSarifFile(string filePath, List<object> violations, List<object> rules)
    {
        try
        {
            var json = File.ReadAllBytes(filePath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("runs", out var runs))
                return;

            foreach (var run in runs.EnumerateArray())
            {
                // Extract rules from the tool driver
                var ruleMap = new Dictionary<string, (string Severity, string Name, string Description, string Tags, string Precision)>();
                if (run.TryGetProperty("tool", out var tool) &&
                    tool.TryGetProperty("driver", out var driver) &&
                    driver.TryGetProperty("rules", out var rulesArray))
                {
                    foreach (var rule in rulesArray.EnumerateArray())
                    {
                        var id = rule.GetProperty("id").GetString() ?? "";
                        var name = rule.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                        var desc = "";
                        if (rule.TryGetProperty("shortDescription", out var sd) && sd.TryGetProperty("text", out var sdt))
                            desc = sdt.GetString() ?? "";
                        else if (rule.TryGetProperty("fullDescription", out var fd) && fd.TryGetProperty("text", out var fdt))
                            desc = fdt.GetString() ?? "";

                        var severity = "warning";
                        var precision = "";
                        if (rule.TryGetProperty("properties", out var props))
                        {
                            if (props.TryGetProperty("problem.severity", out var sev))
                                severity = sev.GetString() ?? "warning";
                            if (props.TryGetProperty("precision", out var prec))
                                precision = prec.GetString() ?? "";
                        }

                        var tags = "";
                        if (rule.TryGetProperty("properties", out var p2) && p2.TryGetProperty("tags", out var tagsArr))
                        {
                            var tagList = new List<string>();
                            foreach (var t in tagsArr.EnumerateArray())
                                tagList.Add(t.GetString() ?? "");
                            tags = string.Join(", ", tagList);
                        }

                        ruleMap[id] = (severity, name, desc, tags, precision);
                        rules.Add(new CodeqlRule(id, name, desc, severity, tags, precision));
                    }
                }

                // Extract results
                if (!run.TryGetProperty("results", out var resultsArray))
                    continue;

                foreach (var result in resultsArray.EnumerateArray())
                {
                    var ruleId = result.TryGetProperty("ruleId", out var rid) ? rid.GetString() ?? "" : "";
                    var message = "";
                    if (result.TryGetProperty("message", out var msg) && msg.TryGetProperty("text", out var msgText))
                        message = msgText.GetString() ?? "";

                    var severity = "warning";
                    if (ruleMap.TryGetValue(ruleId, out var ruleInfo))
                        severity = ruleInfo.Severity;
                    if (result.TryGetProperty("level", out var lvl))
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
                        severity = "info";

                    var file = "";
                    int startLine = 0;

                    if (result.TryGetProperty("locations", out var locs))
                    {
                        foreach (var loc in locs.EnumerateArray())
                        {
                            if (loc.TryGetProperty("physicalLocation", out var physLoc))
                            {
                                if (physLoc.TryGetProperty("artifactLocation", out var artLoc) &&
                                    artLoc.TryGetProperty("uri", out var uri))
                                    file = uri.GetString() ?? "";

                                if (physLoc.TryGetProperty("region", out var region))
                                {
                                    startLine = region.TryGetProperty("startLine", out var sl) ? sl.GetInt32() : 0;
                                }
                            }
                            break; // only use first location
                        }
                    }

                    // Combine RuleId into Message
                    var fullMessage = string.IsNullOrEmpty(ruleId) ? message : $"{ruleId}: {message}";
                    violations.Add(new CodeqlViolation(file, startLine, severity, fullMessage, "codeql"));
                }
            }
        }
        catch (JsonException)
        {
            // Skip malformed SARIF files
        }
    }

    public override string ToString() => "CodeqlProvider";
}

/// <summary>CodeQL analysis violation (mapped from SARIF finding)</summary>
public record CodeqlViolation(
    string File,
    int Line,
    string Severity,
    string Message,
    string Source);

/// <summary>CodeQL rule definition from SARIF</summary>
public record CodeqlRule(
    string Id,
    string Name,
    string Description,
    string Severity,
    string Tags,
    string Precision);
