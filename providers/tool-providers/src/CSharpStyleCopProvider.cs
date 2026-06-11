using System.Text.RegularExpressions;
using Cop.Core;

namespace Cop.Providers.Tools;

/// <summary>
/// Runs 'dotnet build' and parses MSBuild SA*/CS* diagnostic output into violations.
/// </summary>
public class CSharpStyleCopProvider : ToolProvider
{
    private static readonly Regex DiagnosticPattern = new(
        @"^(.+?)\((\d+),(\d+)\):\s+(warning|error)\s+(\w+):\s+(.+?)(?:\s+\[.+\])?$",
        RegexOptions.Compiled);

    protected override string ToolName => "dotnet";

    protected override List<object> RunTool(string rootPath, IReadOnlySet<string> excluded)
    {
        // Clean first to force analyzer re-run
        RunProcess("dotnet", "clean --nologo -v q", rootPath);

        var (stdout, stderr, _) = RunProcess("dotnet", "build -consoleloggerparameters:NoSummary", rootPath);
        var output = stdout + "\n" + stderr;

        var violations = new List<object>();
        foreach (var rawLine in output.Split('\n'))
        {
            var match = DiagnosticPattern.Match(rawLine.Trim());
            if (!match.Success) continue;

            var ruleId = match.Groups[5].Value;
            if (!ruleId.StartsWith("SA") && !ruleId.StartsWith("CS")) continue;

            var filePath = NormalizePath(match.Groups[1].Value, rootPath);
            if (string.IsNullOrEmpty(filePath) || IsExcluded(filePath, excluded)) continue;

            violations.Add(new ToolViolation
            {
                File = filePath,
                Line = int.Parse(match.Groups[2].Value),
                Severity = match.Groups[4].Value.ToLowerInvariant(),
                Message = $"{ruleId}: {match.Groups[6].Value}",
                Source = "stylecop"
            });
        }
        return violations;
    }
}
