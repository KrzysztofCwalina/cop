using System.Text;

namespace Cop.Providers.Dockerfile;

/// <summary>
/// Small dependency-free Dockerfile parser for static analysis. It recognizes comments,
/// blank lines, line continuations, instructions, and FROM stages with optional AS aliases.
/// </summary>
public static class DockerfileParser
{
    public record DockerfileParseResult(List<DockerInstructionInfo> Instructions, List<DockerStageInfo> Stages);

    public static DockerfileParseResult Parse(string text)
    {
        var instructions = new List<DockerInstructionInfo>();
        var stages = new List<DockerStageInfo>();
        var lines = text.Split('\n');

        var logical = new StringBuilder();
        int logicalLine = 0;

        for (int i = 0; i < lines.Length; i++)
        {
            var raw = lines[i].TrimEnd('\r');
            var withoutComment = StripInlineComment(raw).TrimEnd();
            var trimmed = withoutComment.TrimStart();

            if (logical.Length == 0 && trimmed.Length == 0)
                continue;

            if (logical.Length == 0)
                logicalLine = i + 1;

            bool continued = EndsWithContinuation(withoutComment);
            var part = continued ? withoutComment.TrimEnd()[..^1] : withoutComment;

            if (logical.Length > 0)
                logical.Append(' ');
            logical.Append(part.Trim());

            if (!continued)
            {
                AddInstruction(logical.ToString(), logicalLine, instructions, stages);
                logical.Clear();
                logicalLine = 0;
            }
        }

        if (logical.Length > 0)
            AddInstruction(logical.ToString(), logicalLine, instructions, stages);

        return new DockerfileParseResult(instructions, stages);
    }

    private static void AddInstruction(string text, int line, List<DockerInstructionInfo> instructions, List<DockerStageInfo> stages)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
            return;

        int split = IndexOfWhitespace(trimmed);
        var keyword = split < 0 ? trimmed : trimmed[..split];
        if (keyword.Length == 0 || !keyword.All(c => char.IsLetter(c)))
            return;

        var instruction = keyword.ToUpperInvariant();
        var argument = split < 0 ? "" : trimmed[(split + 1)..].Trim();
        int stage = stages.Count == 0 ? -1 : stages.Count - 1;

        if (instruction == "FROM")
        {
            var (image, name) = ParseFrom(argument);
            stage = stages.Count;
            stages.Add(new DockerStageInfo(name, image, stage, line));
        }

        instructions.Add(new DockerInstructionInfo(instruction, argument, line, stage));
    }

    private static (string Image, string Name) ParseFrom(string argument)
    {
        var tokens = SplitTokens(argument);
        string image = "";
        int imageIndex = -1;

        for (int i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].StartsWith("--", StringComparison.Ordinal))
                continue;
            image = tokens[i];
            imageIndex = i;
            break;
        }

        string name = "";
        for (int i = imageIndex + 1; i + 1 < tokens.Count; i++)
        {
            if (tokens[i].Equals("AS", StringComparison.OrdinalIgnoreCase))
            {
                name = tokens[i + 1];
                break;
            }
        }

        return (image, name);
    }

    private static List<string> SplitTokens(string s)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        char quote = '\0';

        foreach (var c in s)
        {
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

    private static int IndexOfWhitespace(string s)
    {
        for (int i = 0; i < s.Length; i++)
            if (char.IsWhiteSpace(s[i]))
                return i;
        return -1;
    }

    private static bool EndsWithContinuation(string s)
    {
        var trimmed = s.TrimEnd();
        return trimmed.Length > 0 && trimmed[^1] == '\\';
    }

    private static string StripInlineComment(string s)
    {
        char quote = '\0';
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                continue;
            }
            if (c == '\'' || c == '"') { quote = c; continue; }
            if (c == '#' && (i == 0 || char.IsWhiteSpace(s[i - 1])))
                return s[..i];
        }
        return s;
    }
}
