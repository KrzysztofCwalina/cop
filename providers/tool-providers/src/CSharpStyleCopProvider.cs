using Cop.Core;

namespace Cop.Providers.Tools;

/// <summary>
/// Runs 'dotnet build' and parses MSBuild SA*/CS* diagnostic output into violations.
/// </summary>
public class CSharpStyleCopProvider : ToolProvider
{
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
            if (!TryParseDiagnostic(rawLine.Trim(), out var diagnosticFilePath, out var diagnosticLine, out var severity, out var ruleId, out var message)) continue;

            if (!ruleId.StartsWith("SA") && !ruleId.StartsWith("CS")) continue;

            var filePath = NormalizePath(diagnosticFilePath, rootPath);
            if (string.IsNullOrEmpty(filePath) || IsExcluded(filePath, excluded)) continue;

            violations.Add(new ToolViolation
            {
                File = filePath,
                Line = int.Parse(diagnosticLine),
                Severity = severity.ToLowerInvariant(),
                Message = $"{ruleId}: {message}",
                Source = "stylecop"
            });
        }
        return violations;
    }

    private static bool TryParseDiagnostic(
        string line,
        out string filePath,
        out string lineNumber,
        out string severity,
        out string ruleId,
        out string message)
    {
        filePath = string.Empty;
        lineNumber = string.Empty;
        severity = string.Empty;
        ruleId = string.Empty;
        message = string.Empty;

        for (var markerStart = 1; markerStart < line.Length; markerStart++)
        {
            if (line[markerStart] != '(') continue;

            var lineStart = markerStart + 1;
            var lineEnd = ReadDigits(line, lineStart);
            if (lineEnd == lineStart || lineEnd >= line.Length || line[lineEnd] != ',') continue;

            var columnStart = lineEnd + 1;
            var columnEnd = ReadDigits(line, columnStart);
            if (columnEnd == columnStart || columnEnd + 1 >= line.Length || line[columnEnd] != ')' || line[columnEnd + 1] != ':') continue;

            var position = columnEnd + 2;
            if (!ReadWhitespace(line, ref position)) return false;

            if (TryReadToken(line, ref position, "warning"))
            {
                severity = "warning";
            }
            else if (TryReadToken(line, ref position, "error"))
            {
                severity = "error";
            }
            else
            {
                return false;
            }

            if (!ReadWhitespace(line, ref position)) return false;

            var ruleStart = position;
            while (position < line.Length && IsRuleIdChar(line[position]))
            {
                position++;
            }

            var ruleEnd = position;
            if (ruleEnd == ruleStart || ruleEnd >= line.Length || line[ruleEnd] != ':') return false;

            position++;
            if (!ReadWhitespace(line, ref position) || position >= line.Length) return false;

            filePath = line[..markerStart];
            lineNumber = line[lineStart..lineEnd];
            ruleId = line[ruleStart..ruleEnd];
            message = StripProjectSuffix(line[position..]);
            return true;
        }

        return false;
    }

    private static int ReadDigits(string text, int position)
    {
        while (position < text.Length && char.IsDigit(text[position]))
        {
            position++;
        }

        return position;
    }

    private static bool ReadWhitespace(string text, ref int position)
    {
        var start = position;
        while (position < text.Length && char.IsWhiteSpace(text[position]))
        {
            position++;
        }

        return position > start;
    }

    private static bool TryReadToken(string text, ref int position, string token)
    {
        if (!text.AsSpan(position).StartsWith(token, StringComparison.Ordinal))
        {
            return false;
        }

        position += token.Length;
        return true;
    }

    private static bool IsRuleIdChar(char value) =>
        value is >= 'A' and <= 'Z'
            or >= 'a' and <= 'z'
            or >= '0' and <= '9'
            or '_';

    private static string StripProjectSuffix(string text)
    {
        for (var i = 1; i < text.Length - 1; i++)
        {
            if (text[i] != '[' || !char.IsWhiteSpace(text[i - 1])) continue;
            if (text[^1] == ']' && i + 1 < text.Length - 1)
            {
                var suffixStart = i - 1;
                while (suffixStart > 0 && char.IsWhiteSpace(text[suffixStart - 1]))
                {
                    suffixStart--;
                }

                return text[..suffixStart];
            }
        }

        return text;
    }
}
