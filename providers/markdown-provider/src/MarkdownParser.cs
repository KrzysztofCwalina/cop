// Extracts Markdown headings, sections, links, fences, and a document tree.

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
        string[] lines = sourceText.Split('\n');
        List<HeadingInfo> headings = new List<HeadingInfo>();
        List<LinkInfo> links = new List<LinkInfo>();
        List<FenceBlockInfo> fenceBlocks = new List<FenceBlockInfo>();

        bool inFence = false;
        string? fenceLanguage = null;
        string? fenceTag = null;
        int fenceStart = 0;
        List<string> contentLines = new List<string>();

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].TrimEnd('\r');
            string trimmed = line.TrimStart();
            int lineNumber = i + 1; // 1-based

            if (!inFence)
            {
                if (trimmed.StartsWith("```"))
                {
                    string afterTicks = trimmed[3..].Trim();
                    inFence = true;
                    fenceStart = lineNumber;
                    contentLines.Clear();

                    if (afterTicks.Length > 0)
                    {
                        string[] parts = afterTicks.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
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
                    while (level < trimmed.Length && trimmed[level] == '#')
                    {
                        level++;
                    }

                    if (level <= 6 && level < trimmed.Length && trimmed[level] == ' ')
                    {
                        string text = trimmed[(level + 1)..].Trim();
                        if (text.Length > 0)
                        {
                            headings.Add(new HeadingInfo(level, text, lineNumber));
                        }
                    }
                }

                // Extract inline links: [text](url) — skip image links ![alt](url)
                ExtractLinks(line, lineNumber, links);
            }
            else
            {
                if (trimmed.StartsWith("```") && trimmed.TrimStart('`').TrimEnd().Length == 0)
                {
                    string content = string.Join('\n', contentLines);
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
        List<SectionInfo> sections = BuildSections(headings, lines);

        return new MarkdownDocument(headings, links, fenceBlocks, sections)
        {
            Root = new MarkdownTreeParser(sourceText).Parse()
        };
    }

    private static void ExtractLinks(string line, int lineNumber, List<LinkInfo> links)
    {
        int index = 0;
        while (index < line.Length)
        {
            if (TryReadLink(line, index, out string? text, out string? url, out int nextIndex))
            {
                links.Add(new LinkInfo(url, text, lineNumber));
                index = nextIndex;
            }
            else
            {
                index++;
            }
        }
    }

    private static bool TryReadLink(string line, int start, out string text, out string url, out int nextIndex)
    {
        text = string.Empty;
        url = string.Empty;
        nextIndex = start;

        if (line[start] != '[' || (start > 0 && line[start - 1] == '!'))
        {
            return false;
        }

        int textStart = start + 1;
        int textEnd = line.IndexOf(']', textStart);
        if (textEnd <= textStart)
        {
            return false;
        }

        int openParen = textEnd + 1;
        if (openParen >= line.Length || line[openParen] != '(')
        {
            return false;
        }

        int urlStart = openParen + 1;
        int urlEnd = line.IndexOf(')', urlStart);
        if (urlEnd <= urlStart)
        {
            return false;
        }

        text = line[textStart..textEnd];
        url = line[urlStart..urlEnd];
        nextIndex = urlEnd + 1;
        return true;
    }

    private static List<SectionInfo> BuildSections(List<HeadingInfo> headings, string[] lines)
    {
        List<SectionInfo> sections = new List<SectionInfo>();
        for (int i = 0; i < headings.Count; i++)
        {
            HeadingInfo heading = headings[i];
            int startLine = heading.Line;
            int endLine = (i + 1 < headings.Count) ? headings[i + 1].Line - 1 : lines.Length;

            // Collect content lines (skip the heading line itself)
            List<string> sectionLines = new List<string>();
            for (int j = startLine; j < endLine && j < lines.Length; j++)
            {
                sectionLines.Add(lines[j].TrimEnd('\r'));
            }
            string content = string.Join('\n', sectionLines).Trim();

            sections.Add(new SectionInfo(heading.Text, heading.Level, content, startLine, endLine));
        }
        return sections;
    }
}
