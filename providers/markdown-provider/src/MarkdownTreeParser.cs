namespace Cop.Providers.Markdown;

internal sealed class MarkdownTreeParser(string source)
{
    private readonly string[] _original = source.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
    private readonly record struct Line(string Text, int Number);

    public MarkdownNodeInfo Parse()
    {
        var lines = _original.Select((text, i) => new Line(text, i + 1)).ToList();
        var root = Node("document", "", 0, 1, lines.Count);
        root.Children.AddRange(Blocks(lines, 0));
        return root;
    }

    private List<MarkdownNodeInfo> Blocks(List<Line> lines, int depth)
    {
        if (depth > 128)
            throw new InvalidDataException("Markdown nesting exceeds the supported depth of 128.");
        var result = new List<MarkdownNodeInfo>();
        var sections = new Stack<MarkdownNodeInfo>();
        void Add(MarkdownNodeInfo node)
        {
            if (sections.TryPeek(out var section)) section.Children.Add(node);
            else result.Add(node);
        }
        void Close(int end)
        {
            var section = sections.Pop();
            section.EndLine = Math.Max(section.StartLine, end);
            section.Content = Content(section.StartLine, section.EndLine);
        }

        for (int i = 0; i < lines.Count;)
        {
            if (string.IsNullOrWhiteSpace(lines[i].Text)) { i++; continue; }
            var start = i;
            var text = lines[i].Text;
            var trimmed = text.TrimStart();

            if (IsIndentedCode(text))
            {
                var body = new List<string>();
                while (i < lines.Count && (IsIndentedCode(lines[i].Text)
                    || string.IsNullOrWhiteSpace(lines[i].Text)))
                {
                    var value = lines[i++].Text;
                    var spaces = Indent(value);
                    int remove = spaces < 4 && spaces < value.Length && value[spaces] == '\t'
                        ? spaces + 1 : Math.Min(4, spaces);
                    body.Add(value[remove..]);
                }
                Add(Node("codeBlock", string.Join('\n', body), 0, lines[start].Number, lines[i - 1].Number));
            }
            else if (Heading(lines, i, out var level, out var title, out var headingLines))
            {
                while (sections.TryPeek(out var previous) && previous.Level >= level)
                    Close(lines[i].Number - 1);
                var node = Node("heading", title, level, lines[i].Number, lines[i + headingLines - 1].Number);
                Add(node);
                sections.Push(node);
                i += headingLines;
            }
            else if (Fence(text, out var marker, out var count))
            {
                i++;
                var body = new List<string>();
                while (i < lines.Count && !FenceEnd(lines[i].Text, marker, count))
                    body.Add(lines[i++].Text);
                if (i < lines.Count) i++;
                Add(Node("codeBlock", string.Join('\n', body), 0, lines[start].Number, lines[i - 1].Number));
            }
            else if (!Thematic(text) && ListMarker(text, out var indent, out _, out _))
            {
                var list = Node("list", "", 0, lines[i].Number, lines[i].Number);
                while (i < lines.Count && ListMarker(lines[i].Text, out var nextIndent, out var bodyStart, out _)
                    && nextIndent == indent)
                {
                    var itemStart = i;
                    var childLines = new List<Line> { new(lines[i].Text[bodyStart..], lines[i].Number) };
                    var contentIndent = bodyStart;
                    i++;
                    bool afterBlank = false;
                    while (i < lines.Count)
                    {
                        var current = lines[i].Text;
                        if (string.IsNullOrWhiteSpace(current))
                        {
                            childLines.Add(lines[i++]);
                            afterBlank = true;
                            continue;
                        }
                        var spaces = Indent(current);
                        if (ListMarker(current, out var itemIndent, out _, out _) && itemIndent <= indent)
                            break;
                        if (spaces < contentIndent && (afterBlank || Boundary(lines, i)))
                            break;
                        childLines.Add(new Line(current[Math.Min(spaces, contentIndent)..], lines[i].Number));
                        afterBlank = false;
                        i++;
                    }
                    var item = Node("listItem", "", 0, lines[itemStart].Number, lines[i - 1].Number);
                    item.Children.AddRange(Blocks(childLines, depth + 1));
                    list.Children.Add(item);
                }
                list.EndLine = lines[i - 1].Number;
                list.Content = Content(list.StartLine, list.EndLine);
                Add(list);
            }
            else if (trimmed.StartsWith('>') && Indent(text) <= 3)
            {
                var quoted = new List<Line>();
                while (i < lines.Count && lines[i].Text.TrimStart().StartsWith('>'))
                {
                    var value = lines[i].Text.TrimStart()[1..];
                    if (value.StartsWith(' ')) value = value[1..];
                    quoted.Add(new Line(value, lines[i++].Number));
                }
                var quote = Node("quote", "", 0, lines[start].Number, lines[i - 1].Number);
                quote.Children.AddRange(Blocks(quoted, depth + 1));
                Add(quote);
            }
            else if (trimmed.StartsWith("<!--", StringComparison.Ordinal))
            {
                while (i < lines.Count && !lines[i++].Text.Contains("-->", StringComparison.Ordinal)) { }
                Add(Node("html", Content(lines[start].Number, lines[i - 1].Number), 0,
                    lines[start].Number, lines[i - 1].Number));
            }
            else if (Thematic(text))
            {
                Add(Node("thematicBreak", text.Trim(), 0, lines[i].Number, lines[i].Number));
                i++;
            }
            else
            {
                bool table = i + 1 < lines.Count && text.Contains('|') && TableSeparator(lines[i + 1].Text);
                i++;
                while (i < lines.Count && !string.IsNullOrWhiteSpace(lines[i].Text)
                    && (table ? lines[i].Text.Contains('|') : !Boundary(lines, i)))
                    i++;
                var body = string.Join('\n', lines.GetRange(start, i - start).Select(line => line.Text));
                Add(Node(table ? "table" : "paragraph", body, 0, lines[start].Number, lines[i - 1].Number));
            }
        }
        while (sections.Count > 0) Close(lines.Count == 0 ? 1 : lines[^1].Number);
        return result;
    }

    private MarkdownNodeInfo Node(string kind, string text, int level, int start, int end) =>
        new(kind, text, level, start) { EndLine = end, Content = Content(start, end) };

    private string Content(int start, int end) => string.Join('\n', _original[(start - 1)..end]);

    private static int Indent(string text)
    {
        int i = 0;
        while (i < text.Length && text[i] == ' ') i++;
        return i;
    }

    private static bool IsIndentedCode(string text)
    {
        int spaces = Indent(text);
        return spaces >= 4 || spaces < text.Length && text[spaces] == '\t';
    }

    private static bool Heading(List<Line> lines, int index, out int level, out string title, out int count)
    {
        level = 0;
        title = "";
        count = 1;
        var text = lines[index].Text;
        if (IsIndentedCode(text)) return false;
        var trimmed = text.TrimStart();
        while (level < trimmed.Length && trimmed[level] == '#') level++;
        if (level is >= 1 and <= 6 && (level == trimmed.Length || char.IsWhiteSpace(trimmed[level])))
        {
            title = trimmed[level..].Trim();
            var end = title.TrimEnd('#');
            if (end.Length < title.Length && (end.Length == 0 || char.IsWhiteSpace(end[^1])))
                title = end.TrimEnd();
            return true;
        }
        level = 0;
        if (index + 1 >= lines.Count || trimmed.Length == 0 || Fence(text, out _, out _)
            || ListMarker(text, out _, out _, out _) || trimmed.StartsWith('>')) return false;
        var underline = lines[index + 1].Text.Trim();
        if (IsIndentedCode(lines[index + 1].Text) || underline.Length == 0
            || (underline[0] != '=' && underline[0] != '-') || underline.Any(c => c != underline[0]))
            return false;
        level = underline[0] == '=' ? 1 : 2;
        title = trimmed.TrimEnd();
        count = 2;
        return true;
    }

    private static bool Fence(string text, out char marker, out int count)
    {
        var trimmed = text.TrimStart();
        count = 0;
        marker = trimmed.Length == 0 ? '\0' : trimmed[0];
        if (IsIndentedCode(text) || marker is not ('`' or '~')) return false;
        while (count < trimmed.Length && trimmed[count] == marker) count++;
        return count >= 3 && (marker != '`' || !trimmed[count..].Contains('`'));
    }

    private static bool FenceEnd(string text, char marker, int count)
    {
        if (IsIndentedCode(text)) return false;
        var trimmed = text.Trim();
        return trimmed.Length >= count && trimmed.All(c => c == marker);
    }

    private static bool ListMarker(string text, out int indent, out int bodyStart, out bool ordered)
    {
        indent = Indent(text);
        bodyStart = indent;
        ordered = false;
        if (bodyStart >= text.Length) return false;
        if (text[bodyStart] is '-' or '+' or '*') bodyStart++;
        else
        {
            int start = bodyStart;
            while (bodyStart < text.Length && char.IsAsciiDigit(text[bodyStart])) bodyStart++;
            if (bodyStart == start || bodyStart - start > 9 || bodyStart >= text.Length
                || text[bodyStart] is not ('.' or ')')) return false;
            ordered = true;
            bodyStart++;
        }
        if (bodyStart < text.Length && !char.IsWhiteSpace(text[bodyStart])) return false;
        if (bodyStart < text.Length) bodyStart++;
        return true;
    }

    private static bool Thematic(string text)
    {
        var compact = text.Trim().Replace(" ", "").Replace("\t", "");
        return compact.Length >= 3 && compact[0] is '-' or '*' or '_' && compact.All(c => c == compact[0]);
    }

    private static bool TableSeparator(string text)
    {
        var cells = text.Trim().Trim('|').Split('|');
        return cells.Length > 0 && cells.All(cell =>
        {
            var value = cell.Trim().Trim(':');
            return value.Length >= 3 && value.All(c => c == '-');
        });
    }

    private static bool Boundary(List<Line> lines, int i) =>
        !IsIndentedCode(lines[i].Text) && (Heading(lines, i, out _, out _, out _) || Fence(lines[i].Text, out _, out _)
        || ListMarker(lines[i].Text, out _, out _, out _) || Thematic(lines[i].Text)
        || lines[i].Text.TrimStart().StartsWith('>')
        || lines[i].Text.TrimStart().StartsWith("<!--", StringComparison.Ordinal));
}
