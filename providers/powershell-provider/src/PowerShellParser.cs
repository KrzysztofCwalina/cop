using System.Text;

namespace Cop.Providers.PowerShell;

/// <summary>
/// A small, dependency-free parser for PowerShell scripts focused on static-analysis queries.
/// It handles comments, quoted strings, block comments, backtick continuations, and command separators.
/// </summary>
public static class PowerShellParser
{
    public record PowerShellParseResult(List<PowerShellCommandInfo> Commands, PowerShellScriptInfo Script);

    private static readonly HashSet<string> PowerShellKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "begin", "break", "catch", "class", "clean", "continue", "data", "define", "do", "dynamicparam",
        "else", "elseif", "end", "enum", "exit", "filter", "finally", "for", "foreach", "from", "function",
        "if", "in", "param", "process", "return", "switch", "throw", "trap", "try", "until", "using", "var", "while",
        "workflow"
    };

    public static PowerShellParseResult Parse(string text)
    {
        var commands = new List<PowerShellCommandInfo>();
        var lines = text.Split('\n');

        var logical = new StringBuilder();
        int logicalStartLine = 1;
        bool hasPendingContinuation = false;
        bool inBlockComment = false;
        bool usesStrictMode = false;

        for (int i = 0; i < lines.Length; i++)
        {
            int lineNo = i + 1;
            var raw = lines[i].TrimEnd('\r');
            var stripped = StripComments(raw, ref inBlockComment);

            if (ContainsStrictMode(stripped))
                usesStrictMode = true;

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

        return new PowerShellParseResult(commands, new PowerShellScriptInfo(usesStrictMode));
    }

    private static void AddCommands(List<PowerShellCommandInfo> commands, string logicalLine, int line)
    {
        var text = logicalLine.Trim();
        if (text.Length == 0)
            return;

        foreach (var segment in SplitSimpleCommands(text))
        {
            var name = GetCommandName(segment);
            if (name.Length == 0 || PowerShellKeywords.Contains(name))
                continue;

            commands.Add(new PowerShellCommandInfo(name, text, line));
        }
    }

    private static bool ContainsStrictMode(string line)
    {
        foreach (var segment in SplitSimpleCommands(line))
        {
            var name = GetCommandName(segment);
            if (name.Equals("Set-StrictMode", StringComparison.OrdinalIgnoreCase))
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

            if (c == '`')
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

        while (i < tokens.Count && IsNoiseToken(tokens[i]))
            i++;

        if (i >= tokens.Count)
            return "";

        var name = tokens[i];
        if (IsAssignmentLike(name))
            return "";

        return name;
    }

    private static bool IsNoiseToken(string token)
        => token is "&" or ".";

    private static bool IsAssignmentLike(string token)
        => token.StartsWith("$", StringComparison.Ordinal) || token == "=" || token.EndsWith("=", StringComparison.Ordinal);

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

            if (c == '`')
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

            if (c is '(' or ')' or '{' or '}' or ',')
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

    private static string StripComments(string line, ref bool inBlockComment)
    {
        var result = new StringBuilder(line.Length);
        char quote = '\0';
        bool escaped = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];

            if (inBlockComment)
            {
                if (c == '#' && i + 1 < line.Length && line[i + 1] == '>')
                {
                    inBlockComment = false;
                    i++;
                    result.Append(' ');
                }
                continue;
            }

            if (escaped)
            {
                result.Append(c);
                escaped = false;
                continue;
            }

            if (c == '`')
            {
                result.Append(c);
                escaped = true;
                continue;
            }

            if (quote != '\0')
            {
                result.Append(c);
                if (c == quote)
                    quote = '\0';
                continue;
            }

            if (c == '\'' || c == '"')
            {
                quote = c;
                result.Append(c);
                continue;
            }

            if (c == '<' && i + 1 < line.Length && line[i + 1] == '#')
            {
                inBlockComment = true;
                i++;
                result.Append(' ');
                continue;
            }

            if (c == '#')
                break;

            result.Append(c);
        }

        return result.ToString();
    }

    private static bool EndsWithContinuation(string line)
    {
        int backtickCount = 0;
        for (int i = line.Length - 1; i >= 0 && line[i] == '`'; i--)
            backtickCount++;

        return backtickCount % 2 == 1 && !EndsInsideQuote(line);
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

            if (c == '`')
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
