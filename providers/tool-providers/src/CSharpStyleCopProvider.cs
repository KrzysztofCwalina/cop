// Runs StyleCop analysis and projects diagnostics into provider violations.

using Cop.Core;

namespace Cop.Providers.Tools;

/// <summary>
/// Runs 'dotnet build' and parses MSBuild SA*/CS* diagnostic output into violations.
/// </summary>
public class CSharpStyleCopProvider : ToolProvider
{
    protected override string ToolName => "dotnet";

    protected override async ValueTask<List<object>> RunToolAsync(
        string rootPath, IReadOnlySet<string> excluded, CancellationToken cancellationToken)
    {
        // Clean first to force analyzer re-run
        await RunProcessAsync(new("dotnet", "clean --nologo -v q", rootPath),
            cancellationToken).ConfigureAwait(false);

        (string? stdout, string? stderr, int _) = await RunProcessAsync(
            new("dotnet", "build -consoleloggerparameters:NoSummary", rootPath),
            cancellationToken).ConfigureAwait(false);
        string output = stdout + "\n" + stderr;

        List<object> violations = new List<object>();
        foreach (string rawLine in output.Split('\n'))
        {
            if (!TryParseDiagnostic(rawLine.Trim(), out string? diagnosticFilePath, out string? diagnosticLine, out string? severity, out string? ruleId, out string? message))
            {
                continue;
            }

            if (!ruleId.StartsWith("SA") && !ruleId.StartsWith("CS"))
            {
                continue;
            }

            string filePath = NormalizePath(diagnosticFilePath, rootPath);
            if (string.IsNullOrEmpty(filePath) || IsExcluded(filePath, excluded))
            {
                continue;
            }

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

        for (int markerStart = 1; markerStart < line.Length; markerStart++)
        {
            if (line[markerStart] != '(')
            {
                continue;
            }

            int lineStart = markerStart + 1;
            int lineEnd = ReadDigits(line, lineStart);
            if (lineEnd == lineStart || lineEnd >= line.Length || line[lineEnd] != ',')
            {
                continue;
            }

            int columnStart = lineEnd + 1;
            int columnEnd = ReadDigits(line, columnStart);
            if (columnEnd == columnStart || columnEnd + 1 >= line.Length || line[columnEnd] != ')' || line[columnEnd + 1] != ':')
            {
                continue;
            }

            int position = columnEnd + 2;
            if (!ReadWhitespace(line, ref position))
            {
                return false;
            }

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

            if (!ReadWhitespace(line, ref position))
            {
                return false;
            }

            int ruleStart = position;
            while (position < line.Length && IsRuleIdChar(line[position]))
            {
                position++;
            }

            int ruleEnd = position;
            if (ruleEnd == ruleStart || ruleEnd >= line.Length || line[ruleEnd] != ':')
            {
                return false;
            }

            position++;
            if (!ReadWhitespace(line, ref position) || position >= line.Length)
            {
                return false;
            }

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
        int start = position;
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
        for (int i = 1; i < text.Length - 1; i++)
        {
            if (text[i] != '[' || !char.IsWhiteSpace(text[i - 1]))
            {
                continue;
            }

            if (text[^1] == ']' && i + 1 < text.Length - 1)
            {
                int suffixStart = i - 1;
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
