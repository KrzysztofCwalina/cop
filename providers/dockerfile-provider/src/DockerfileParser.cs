using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

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
        char escape = '\\';
        bool seenInstruction = false;

        for (int i = 0; i < lines.Length; i++)
        {
            var raw = lines[i].TrimEnd('\r');
            if (raw.TrimStart().StartsWith('#'))
            {
                if (!seenInstruction && Regex.Match(raw, @"^\s*#\s*escape\s*=\s*([\\`])\s*$", RegexOptions.IgnoreCase) is { Success: true } directive)
                    escape = directive.Groups[1].Value[0];
                continue;
            }
            var withoutComment = raw.TrimEnd();
            var trimmed = withoutComment.TrimStart();

            if (logical.Length == 0 && trimmed.Length == 0)
                continue;

            if (logical.Length == 0)
                logicalLine = i + 1;

            bool continued = EndsWithContinuation(withoutComment, escape);
            var part = continued ? withoutComment.TrimEnd()[..^1] : withoutComment;

            if (logical.Length > 0)
                logical.Append(' ');
            logical.Append(part.Trim());

            if (!continued)
            {
                seenInstruction = true;
                var header = logical.ToString();
                if (header.StartsWith("RUN ", StringComparison.OrdinalIgnoreCase)
                    || header.StartsWith("COPY ", StringComparison.OrdinalIgnoreCase)
                    || header.StartsWith("ADD ", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (Match heredoc in Regex.Matches(header, """<<(-?)(['"]?)([A-Za-z_][A-Za-z0-9_]*)\2"""))
                    {
                        var delimiter = heredoc.Groups[3].Value;
                        bool stripTabs = heredoc.Groups[1].Length > 0;
                        bool closed = false;
                        while (++i < lines.Length)
                        {
                            var content = lines[i].TrimEnd('\r');
                            if ((stripTabs ? content.TrimStart('\t') : content) == delimiter)
                            { closed = true; break; }
                            logical.Append('\n').Append(content);
                        }
                        if (!closed) Cop.Core.ProviderErrors.Report($"Dockerfile({logicalLine}): unterminated heredoc '{delimiter}'");
                    }
                }
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

        var tokens = SplitTokens(argument);
        var copyFrom = instruction is "COPY" or "ADD"
            ? tokens.FirstOrDefault(t => t.StartsWith("--from=", StringComparison.OrdinalIgnoreCase))?["--from=".Length..]
            : null;
        int referencedStage = -1;
        if (copyFrom is not null)
            referencedStage = stages.Take(Math.Max(0, stage)).ToList().FindIndex(s => s.Name.Equals(copyFrom, StringComparison.OrdinalIgnoreCase)
                || s.Index.ToString(System.Globalization.CultureInfo.InvariantCulture) == copyFrom);
        if (instruction == "FROM")
            referencedStage = stages.Take(stage).ToList().FindIndex(s => s.Name.Length > 0
                && s.Name.Equals(stages[stage].Image, StringComparison.OrdinalIgnoreCase));

        var arguments = new List<string>();
        var jsonArgument = Regex.Replace(argument, @"^(?:--[^\s]+\s+)+", "");
        bool jsonForm = jsonArgument.StartsWith('[');
        if (jsonForm && instruction is "RUN" or "CMD" or "ENTRYPOINT" or "SHELL" or "COPY" or "ADD")
        {
            try { arguments = JsonSerializer.Deserialize<List<string>>(jsonArgument) ?? []; }
            catch (JsonException ex) { Cop.Core.ProviderErrors.Report($"Dockerfile({line}): invalid JSON instruction: {ex.Message}"); }
        }
        instructions.Add(new DockerInstructionInfo(instruction, argument, line, stage)
        {
            IsJsonForm = jsonForm, Arguments = arguments, From = copyFrom, ReferencedStage = referencedStage
        });
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

    private static bool EndsWithContinuation(string s, char escape)
    {
        var trimmed = s.TrimEnd();
        return trimmed.Length > 0 && trimmed[^1] == escape;
    }
}
