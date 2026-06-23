using System.Text;

namespace Cop.Providers.Bash;

/// <summary>
/// A small, dependency-free parser for shell scripts focused on static-analysis queries.
/// It handles comments, simple quotes, line continuations, and command separators.
/// </summary>
public static class BashParser
{
    public record BashParseResult(List<ShellCommandInfo> Commands, ShellScriptInfo Script);

    private static readonly HashSet<string> ShellKeywords = new(StringComparer.Ordinal)
    {
        "!", "[", "[[", "{", "}", "case", "do", "done", "elif", "else", "esac", "fi",
        "for", "function", "if", "in", "select", "then", "time", "until", "while"
    };

    public static BashParseResult Parse(string text)
    {
        var commands = new List<ShellCommandInfo>();
        var lines = text.Split('\n');

        var logical = new StringBuilder();
        int logicalStartLine = 1;
        bool hasPendingContinuation = false;

        for (int i = 0; i < lines.Length; i++)
        {
            int lineNo = i + 1;
            var raw = lines[i].TrimEnd('\r');
            var stripped = StripComment(raw, lineNo);

            if (!hasPendingContinuation)
                logicalStartLine = lineNo;

            var working = stripped.TrimEnd();
            bool continued = EndsWithContinuation(working);
            if (continued)
                working = working[..^1].TrimEnd();

            if (logical.Length > 0 && working.Length > 0)
                logical.Append(' ');
            logical.Append(working);

            hasPendingContinuation = continued;
            if (continued)
                continue;

            AddCommands(commands, logical.ToString(), logicalStartLine);
            logical.Clear();
        }

        if (logical.Length > 0)
            AddCommands(commands, logical.ToString(), logicalStartLine);

        return new BashParseResult(commands, new ShellScriptInfo(HasStrictMode(lines)));
    }

    private static void AddCommands(List<ShellCommandInfo> commands, string logicalLine, int line)
    {
        var text = logicalLine.Trim();
        if (text.Length == 0)
            return;

        if (line == 1 && text.StartsWith("#!", StringComparison.Ordinal))
            return;

        foreach (var segment in SplitSimpleCommands(text))
        {
            var name = GetCommandName(segment);
            if (name.Length == 0 || ShellKeywords.Contains(name))
                continue;

            commands.Add(new ShellCommandInfo(name, text, line));
        }
    }

    private static bool HasStrictMode(string[] lines)
    {
        int inspected = 0;
        for (int i = 0; i < lines.Length && inspected < 10; i++)
        {
            int lineNo = i + 1;
            var trimmed = StripComment(lines[i].TrimEnd('\r'), lineNo).Trim();
            if (trimmed.Length == 0 || (lineNo == 1 && trimmed.StartsWith("#!", StringComparison.Ordinal)))
                continue;

            inspected++;
            if (!trimmed.StartsWith("set ", StringComparison.Ordinal))
                continue;

            if (trimmed.Contains("-euo pipefail", StringComparison.Ordinal)
                || trimmed.Contains("-eo pipefail", StringComparison.Ordinal)
                || trimmed.Contains("-e ", StringComparison.Ordinal)
                || trimmed.EndsWith("-e", StringComparison.Ordinal)
                || trimmed.StartsWith("set -e", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static List<string> SplitSimpleCommands(string text)
    {
        var parts = new List<string>();
        int start = 0;
        char quote = '\0';
        bool escaped = false;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];

            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (c == '\\')
            {
                escaped = true;
                continue;
            }

            if (quote != '\0')
            {
                if (c == quote)
                    quote = '\0';
                continue;
            }

            if (c == '\'' || c == '"')
            {
                quote = c;
                continue;
            }

            int separatorLength = 0;
            if (c == ';' || c == '|')
                separatorLength = (c == '|' && i + 1 < text.Length && text[i + 1] == '|') ? 2 : 1;
            else if (c == '&' && i + 1 < text.Length && text[i + 1] == '&')
                separatorLength = 2;

            if (separatorLength == 0)
                continue;

            AddPart(parts, text[start..i]);
            i += separatorLength - 1;
            start = i + 1;
        }

        AddPart(parts, text[start..]);
        return parts;
    }

    private static void AddPart(List<string> parts, string part)
    {
        var trimmed = part.Trim();
        if (trimmed.Length > 0)
            parts.Add(trimmed);
    }

    private static string GetCommandName(string segment)
    {
        var tokens = Tokenize(segment);
        int i = 0;

        while (i < tokens.Count && IsEnvironmentAssignment(tokens[i]))
            i++;

        if (i < tokens.Count && tokens[i] == "sudo")
            i++;

        if (i < tokens.Count && tokens[i] == "env")
        {
            i++;
            while (i < tokens.Count && (IsEnvironmentAssignment(tokens[i]) || tokens[i].StartsWith("-", StringComparison.Ordinal)))
                i++;
        }

        if (i >= tokens.Count)
            return "";

        var name = tokens[i];
        if (name.EndsWith("()", StringComparison.Ordinal))
            return "";

        return name;
    }

    private static List<string> Tokenize(string segment)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        char quote = '\0';
        bool escaped = false;

        foreach (char c in segment)
        {
            if (escaped)
            {
                current.Append(c);
                escaped = false;
                continue;
            }

            if (c == '\\')
            {
                escaped = true;
                continue;
            }

            if (quote != '\0')
            {
                if (c == quote)
                    quote = '\0';
                else
                    current.Append(c);
                continue;
            }

            if (c == '\'' || c == '"')
            {
                quote = c;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0)
            tokens.Add(current.ToString());

        return tokens;
    }

    private static bool IsEnvironmentAssignment(string token)
    {
        int equals = token.IndexOf('=');
        if (equals <= 0)
            return false;

        for (int i = 0; i < equals; i++)
        {
            char c = token[i];
            if (!(char.IsLetterOrDigit(c) || c == '_') || (i == 0 && char.IsDigit(c)))
                return false;
        }

        return true;
    }

    private static string StripComment(string line, int lineNo)
    {
        if (lineNo == 1 && line.StartsWith("#!", StringComparison.Ordinal))
            return line;

        char quote = '\0';
        bool escaped = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];

            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (c == '\\')
            {
                escaped = true;
                continue;
            }

            if (quote != '\0')
            {
                if (c == quote)
                    quote = '\0';
                continue;
            }

            if (c == '\'' || c == '"')
            {
                quote = c;
                continue;
            }

            if (c == '#')
                return line[..i];
        }

        return line;
    }

    private static bool EndsWithContinuation(string line)
    {
        int backslashCount = 0;
        for (int i = line.Length - 1; i >= 0 && line[i] == '\\'; i--)
            backslashCount++;

        return backslashCount % 2 == 1 && !EndsInsideQuote(line);
    }

    private static bool EndsInsideQuote(string line)
    {
        char quote = '\0';
        bool escaped = false;
        foreach (char c in line)
        {
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (c == '\\')
            {
                escaped = true;
                continue;
            }

            if (quote != '\0')
            {
                if (c == quote)
                    quote = '\0';
                continue;
            }

            if (c == '\'' || c == '"')
                quote = c;
        }

        return quote != '\0';
    }
}

