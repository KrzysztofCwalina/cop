using System.Text.RegularExpressions;

namespace Cop.Providers.Markdown;

/// <summary>
/// Parses markdown text into structured elements: headings, links, sections, fence blocks.
/// All extraction runs from raw text. Elements inside fenced code blocks are excluded
/// from heading/link extraction to avoid false positives.
/// </summary>
public static class MarkdownParser
{
    /// <summary>
    /// Parses all markdown elements from source text in a single pass.
    /// Returns a cached result that can be used to derive individual collections.
    /// </summary>
    public static MarkdownDocument Parse(string sourceText)
    {
        var lines = sourceText.Split('\n');
        var headings = new List<HeadingInfo>();
        var links = new List<LinkInfo>();
        var fenceBlocks = new List<FenceBlockInfo>();

        bool inFence = false;
        string? fenceLanguage = null;
        string? fenceTag = null;
        int fenceStart = 0;
        var contentLines = new List<string>();

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            var trimmed = line.TrimStart();
            int lineNumber = i + 1; // 1-based

            if (!inFence)
            {
                if (trimmed.StartsWith("```"))
                {
                    var afterTicks = trimmed[3..].Trim();
                    inFence = true;
                    fenceStart = lineNumber;
                    contentLines.Clear();

                    if (afterTicks.Length > 0)
                    {
                        var parts = afterTicks.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                        fenceLanguage = parts[0];
                        fenceTag = parts.Length > 1 ? parts[1] : null;
                    }
                    else
                    {
                        fenceLanguage = null;
                        fenceTag = null;
                    }
                    continue;
                }

                // Extract headings (ATX-style: # Heading)
                if (trimmed.StartsWith('#'))
                {
                    int level = 0;
                    while (level < trimmed.Length && trimmed[level] == '#') level++;
                    if (level <= 6 && level < trimmed.Length && trimmed[level] == ' ')
                    {
                        var text = trimmed[(level + 1)..].Trim();
                        if (text.Length > 0)
                            headings.Add(new HeadingInfo(level, text, lineNumber));
                    }
                }

                // Extract inline links: [text](url) — skip image links ![alt](url)
                ExtractLinks(line, lineNumber, links);
            }
            else
            {
                if (trimmed.StartsWith("```") && trimmed.TrimStart('`').TrimEnd().Length == 0)
                {
                    var content = string.Join('\n', contentLines);
                    fenceBlocks.Add(new FenceBlockInfo(fenceLanguage, fenceTag, fenceStart, lineNumber, content));
                    inFence = false;
                    fenceLanguage = null;
                    fenceTag = null;
                }
                else
                {
                    contentLines.Add(line);
                }
            }
        }

        // Build sections from headings
        var sections = BuildSections(headings, lines);

        return new MarkdownDocument(headings, links, fenceBlocks, sections);
    }

    private static readonly Regex LinkPattern = new(@"(?<!!)\[([^\]]+)\]\(([^)]+)\)", RegexOptions.Compiled);

    private static void ExtractLinks(string line, int lineNumber, List<LinkInfo> links)
    {
        var matches = LinkPattern.Matches(line);
        foreach (Match match in matches)
        {
            var text = match.Groups[1].Value;
            var url = match.Groups[2].Value;
            links.Add(new LinkInfo(url, text, lineNumber));
        }
    }

    private static List<SectionInfo> BuildSections(List<HeadingInfo> headings, string[] lines)
    {
        var sections = new List<SectionInfo>();
        for (int i = 0; i < headings.Count; i++)
        {
            var heading = headings[i];
            int startLine = heading.Line;
            int endLine = (i + 1 < headings.Count) ? headings[i + 1].Line - 1 : lines.Length;

            // Collect content lines (skip the heading line itself)
            var sectionLines = new List<string>();
            for (int j = startLine; j < endLine && j < lines.Length; j++)
            {
                sectionLines.Add(lines[j].TrimEnd('\r'));
            }
            var content = string.Join('\n', sectionLines).Trim();

            sections.Add(new SectionInfo(heading.Text, heading.Level, content, startLine, endLine));
        }
        return sections;
    }
}

/// <summary>
/// Cached parse result for a single markdown file.
/// </summary>
public record MarkdownDocument(
    List<HeadingInfo> Headings,
    List<LinkInfo> Links,
    List<FenceBlockInfo> FenceBlocks,
    List<SectionInfo> Sections);
