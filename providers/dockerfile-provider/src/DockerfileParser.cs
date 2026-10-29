using System.Text;
using System.Text.Json;

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
                if (!seenInstruction && TryGetEscapeDirective(raw, out var directive))
                    escape = directive;
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
                    foreach (var (delimiter, stripTabs) in HeredocDelimiters(header))
                    {
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
        var jsonArgument = WithoutLeadingFlags(argument);
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

    private static bool TryGetEscapeDirective(string text, out char escape)
    {
        escape = default;
        var directive = text.AsSpan().TrimStart();
        if (directive.IsEmpty || directive[0] != '#') return false;
        directive = directive[1..].Trim();
        int equals = directive.IndexOf('=');
        if (equals < 0 || !directive[..equals].TrimEnd().Equals("escape", StringComparison.OrdinalIgnoreCase))
            return false;
        var value = directive[(equals + 1)..].Trim();
        if (value.Length != 1 || value[0] is not ('\\' or '`')) return false;
        escape = value[0];
        return true;
    }

    private static IEnumerable<(string Delimiter, bool StripTabs)> HeredocDelimiters(string header)
    {
        for (int i = 0; i + 1 < header.Length; i++)
        {
            if (header[i] != '<' || header[i + 1] != '<') continue;
            int position = i + 2;
            bool stripTabs = position < header.Length && header[position] == '-';
            if (stripTabs) position++;
            char quote = position < header.Length && header[position] is '\'' or '"' ? header[position++] : '\0';
            int start = position;
            if (position == header.Length || !IsDelimiterStart(header[position])) continue;
            while (++position < header.Length && (IsDelimiterStart(header[position]) || char.IsAsciiDigit(header[position]))) { }
            if (quote != '\0' && (position == header.Length || header[position] != quote)) continue;
            yield return (header[start..position], stripTabs);
            i = quote == '\0' ? position - 1 : position;
        }
    }

    private static bool IsDelimiterStart(char value) => char.IsAsciiLetter(value) || value == '_';

    private static string WithoutLeadingFlags(string argument)
    {
        int start = 0;
        while (argument.AsSpan(start).StartsWith("--", StringComparison.Ordinal))
        {
            int end = start + 2;
            while (end < argument.Length && !char.IsWhiteSpace(argument[end])) end++;
            if (end == start + 2 || end == argument.Length) break;
            while (end < argument.Length && char.IsWhiteSpace(argument[end])) end++;
            start = end;
        }
        return argument[start..];
    }

    private static bool EndsWithContinuation(string s, char escape)
    {
        var trimmed = s.TrimEnd();
        return trimmed.Length > 0 && trimmed[^1] == escape;
    }
}
