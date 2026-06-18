namespace Cop.Cli.Commands;

/// <summary>
/// Renders markdown text with ANSI color codes for terminal display.
/// Handles headers, code blocks, inline code, bold, and tables.
/// </summary>
internal static class ConsoleMarkdown
{
    // ANSI escape codes
    internal const string Bold = "\x1b[1m";
    internal const string Dim = "\x1b[2m";
    internal const string Cyan = "\x1b[36m";
    internal const string Yellow = "\x1b[33m";
    internal const string Green = "\x1b[32m";
    internal const string Gray = "\x1b[90m";
    internal const string White = "\x1b[37m";
    internal const string Reset = "\x1b[0m";

    /// <summary>
    /// Set by the --no-color CLI flag to force plain output (replaces the former NO_COLOR env var).
    /// </summary>
    internal static bool NoColor;

    /// <summary>
    /// Returns true if ANSI colors should be used: only when writing to a real terminal
    /// (not redirected/piped) and --no-color was not passed.
    /// </summary>
    internal static bool UseColor =>
        !Console.IsOutputRedirected &&
        !NoColor;

    /// <summary>
    /// Writes markdown content to the console with ANSI colorization.
    /// Falls back to plain text when output is redirected or NO_COLOR is set.
    /// </summary>
    public static void WriteMarkdown(string markdown, TextWriter? writer = null)
    {
        writer ??= Console.Out;
        bool color = UseColor && writer == Console.Out;

        var lines = markdown.Split('\n');
        bool inCodeBlock = false;

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');

            if (line.StartsWith("```"))
            {
                inCodeBlock = !inCodeBlock;
                // Don't display code fence markers
                continue;
            }

            if (inCodeBlock)
            {
                // Code block content: leave as default terminal color
                writer.WriteLine(line);
                continue;
            }

            if (!color)
            {
                // Strip markdown syntax even in plain text
                if (line.StartsWith("#### "))
                    writer.WriteLine(line[5..]);
                else if (line.StartsWith("### "))
                    writer.WriteLine(line[4..]);
                else if (line.StartsWith("## "))
                    writer.WriteLine(line[3..]);
                else if (line.StartsWith("# "))
                    writer.WriteLine(line[2..]);
                else if (line.StartsWith("> "))
                    writer.WriteLine(StripInlineMarkdown(line[2..]));
                else
                    writer.WriteLine(StripInlineMarkdown(line));
                continue;
            }

            // Headers — strip # prefix, show as styled text
            if (line.StartsWith("#### "))
            {
                writer.WriteLine($"{Bold}{line[5..]}{Reset}");
                continue;
            }
            if (line.StartsWith("### "))
            {
                writer.WriteLine($"{Bold}{line[4..]}{Reset}");
                continue;
            }
            if (line.StartsWith("## "))
            {
                writer.WriteLine($"{Bold}{Cyan}{line[3..]}{Reset}");
                continue;
            }
            if (line.StartsWith("# "))
            {
                writer.WriteLine($"{Bold}{Cyan}{line[2..]}{Reset}");
                continue;
            }

            // Table separator lines (|---|---|)
            if (IsTableSeparator(line))
            {
                writer.WriteLine($"{Gray}{line}{Reset}");
                continue;
            }

            // Table header line (first row before separator)
            if (line.StartsWith('|') && line.EndsWith('|'))
            {
                writer.Write(RenderInlineFormatting(line));
                writer.WriteLine(Reset);
                continue;
            }

            // Block quote lines — strip > prefix
            if (line.StartsWith("> "))
            {
                writer.WriteLine($"{Gray}{line[2..]}{Reset}");
                continue;
            }

            // Regular lines with inline formatting
            writer.Write(RenderInlineFormatting(line));
            writer.WriteLine(Reset);
        }
    }

    /// <summary>
    /// Renders inline markdown formatting (backticks, bold) with ANSI codes.
    /// </summary>
    private static string RenderInlineFormatting(string line)
    {
        var sb = new System.Text.StringBuilder(line.Length + 64);
        int i = 0;

        while (i < line.Length)
        {
            // Inline code: `text`
            if (line[i] == '`')
            {
                int end = line.IndexOf('`', i + 1);
                if (end > i)
                {
                    var code = line.Substring(i + 1, end - i - 1);
                    sb.Append(Cyan).Append(code).Append(Reset);
                    i = end + 1;
                    continue;
                }
            }

            // Bold: **text**
            if (i + 1 < line.Length && line[i] == '*' && line[i + 1] == '*')
            {
                int end = line.IndexOf("**", i + 2, StringComparison.Ordinal);
                if (end > i)
                {
                    var text = line.Substring(i + 2, end - i - 2);
                    sb.Append(Bold).Append(text).Append(Reset);
                    i = end + 2;
                    continue;
                }
            }

            sb.Append(line[i]);
            i++;
        }

        return sb.ToString();
    }

    private static bool IsTableSeparator(string line)
    {
        if (!line.StartsWith('|') || !line.EndsWith('|')) return false;
        for (int i = 1; i < line.Length - 1; i++)
        {
            char c = line[i];
            if (c != '-' && c != '|' && c != ':' && c != ' ') return false;
        }
        return true;
    }

    /// <summary>
    /// Strips inline markdown syntax (backticks, bold markers, link syntax) for plain text output.
    /// </summary>
    private static string StripInlineMarkdown(string line)
    {
        // Remove **bold** markers
        line = line.Replace("**", "");
        // Remove `code` backticks
        line = line.Replace("`", "");
        // Simplify [text](url) links to just text
        int linkStart;
        while ((linkStart = line.IndexOf('[')) >= 0)
        {
            int linkEnd = line.IndexOf(']', linkStart);
            if (linkEnd < 0) break;
            int parenStart = linkEnd + 1;
            if (parenStart < line.Length && line[parenStart] == '(')
            {
                int parenEnd = line.IndexOf(')', parenStart);
                if (parenEnd >= 0)
                {
                    var text = line[(linkStart + 1)..linkEnd];
                    line = line[..linkStart] + text + line[(parenEnd + 1)..];
                    continue;
                }
            }
            break;
        }
        return line;
    }

    /// <summary>
    /// Writes a section header with colorization (no markdown # prefix).
    /// </summary>
    public static void WriteHeader(string title, int level = 2, TextWriter? writer = null)
    {
        writer ??= Console.Out;
        if (UseColor && writer == Console.Out)
            writer.WriteLine($"{Bold}{Cyan}{title}{Reset}");
        else
            writer.WriteLine(title);
    }

    /// <summary>
    /// Writes a keyword-name pair like "type MyType" with keyword colored.
    /// </summary>
    public static void WriteKeywordName(string keyword, string name, TextWriter? writer = null)
    {
        writer ??= Console.Out;
        if (UseColor && writer == Console.Out)
            writer.Write($"{Cyan}{keyword}{Reset} {Bold}{name}{Reset}");
        else
            writer.Write($"{keyword} {name}");
    }

    /// <summary>
    /// Writes a doc comment in gray.
    /// </summary>
    public static void WriteDocComment(string comment, TextWriter? writer = null)
    {
        writer ??= Console.Out;
        if (UseColor && writer == Console.Out)
            writer.Write($"{Gray}# {comment}{Reset}");
        else
            writer.Write($"# {comment}");
    }

    /// <summary>
    /// Writes a type annotation like ": TypeName" in yellow.
    /// </summary>
    public static void WriteTypeAnnotation(string type, TextWriter? writer = null)
    {
        writer ??= Console.Out;
        if (UseColor && writer == Console.Out)
            writer.Write($" : {Yellow}{type}{Reset}");
        else
            writer.Write($" : {type}");
    }

    /// <summary>
    /// Writes dimmed text (for punctuation, separators, etc.)
    /// </summary>
    public static void WriteDim(string text, TextWriter? writer = null)
    {
        writer ??= Console.Out;
        if (UseColor && writer == Console.Out)
            writer.Write($"{Gray}{text}{Reset}");
        else
            writer.Write(text);
    }
}
