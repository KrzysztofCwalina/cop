namespace Cop.Providers.Yaml;

/// <summary>
/// A small, dependency-free block-style YAML parser focused on static analysis of
/// configuration files (CI workflows, Kubernetes manifests, docker-compose). It flattens
/// mapping keys into dotted paths with their scalar values and line numbers.
///
/// Supported: block mappings, block sequences ("- "), scalar values (plain/single/double
/// quoted), comments, multiple documents ("---"), and block scalars ("|" / ">"). Flow style
/// ({a: b}, [1, 2]) is captured as an opaque scalar value rather than expanded. Sequence
/// elements are represented in paths by a "[]" segment (no numeric index).
/// </summary>
public static class YamlParser
{
    public record YamlParseResult(List<YamlEntryInfo> Entries, List<YamlDocumentInfo> Documents);

    private record Frame(int Indent, string Segment);

    public static YamlParseResult Parse(string text)
    {
        var entries = new List<YamlEntryInfo>();
        var documents = new List<YamlDocumentInfo>();
        var lines = text.Split('\n');

        var stack = new List<Frame>();
        int docIndex = 0;
        bool documentOpened = false;

        for (int i = 0; i < lines.Length; i++)
        {
            var raw = lines[i].TrimEnd('\r');
            int lineNo = i + 1;

            var contentTrim = raw.TrimStart(' ');
            if (contentTrim.Length == 0 || contentTrim[0] == '#')
                continue;

            int indent = raw.Length - contentTrim.Length;

            // Document separators.
            if (contentTrim.StartsWith("---"))
            {
                if (documentOpened) docIndex++;
                documents.Add(new YamlDocumentInfo(docIndex, lineNo));
                documentOpened = true;
                stack.Clear();
                // A document may begin inline after the marker (rare); ignore the remainder.
                continue;
            }
            if (contentTrim == "...")
            {
                stack.Clear();
                continue;
            }

            if (!documentOpened)
            {
                documents.Add(new YamlDocumentInfo(docIndex, lineNo));
                documentOpened = true;
            }

            var work = StripInlineComment(contentTrim);
            if (work.Length == 0)
                continue;

            // Sequence item: "- ...." or a bare "-".
            if (work == "-" || work.StartsWith("- "))
            {
                PopTo(stack, indent, inclusive: true);
                string prefix = PathOf(stack);
                string seqPath = prefix.Length == 0 ? "[]" : prefix + "[]";
                // The dash introduces a sequence element; nested keys align past the dash.
                stack.Add(new Frame(indent, "[]"));

                string itemContent = work.Length <= 2 ? "" : work[2..].TrimStart(' ');
                if (itemContent.Length == 0)
                    continue;

                // "- key: value" — the element's first mapping key, sitting after the dash.
                if (TrySplitKeyValue(itemContent, out var seqKey, out var seqValue))
                {
                    int keyIndent = indent + (work.Length - itemContent.Length);
                    string keyPath = seqPath + "." + seqKey;
                    entries.Add(new YamlEntryInfo(keyPath, seqKey, seqValue, lineNo, docIndex));
                    stack.Add(new Frame(keyIndent, seqKey));
                    i = SkipBlockScalar(lines, i, keyIndent, seqValue);
                }
                else
                {
                    // Scalar sequence item.
                    entries.Add(new YamlEntryInfo(seqPath, "", Unquote(itemContent), lineNo, docIndex));
                }
                continue;
            }

            // Mapping entry: "key: value" or "key:".
            if (TrySplitKeyValue(work, out var key, out var value))
            {
                PopTo(stack, indent, inclusive: true);
                string mapPrefix = PathOf(stack);
                string path = mapPrefix.Length == 0 ? key : mapPrefix + "." + key;
                entries.Add(new YamlEntryInfo(path, key, value, lineNo, docIndex));
                stack.Add(new Frame(indent, key));
                i = SkipBlockScalar(lines, i, indent, value);
            }
        }

        return new YamlParseResult(entries, documents);
    }

    // Pops frames whose indent is >= the given indent (siblings and deeper).
    private static void PopTo(List<Frame> stack, int indent, bool inclusive)
    {
        while (stack.Count > 0 && stack[^1].Indent >= indent)
            stack.RemoveAt(stack.Count - 1);
    }

    private static string PathOf(List<Frame> stack)
    {
        if (stack.Count == 0) return "";
        var sb = new System.Text.StringBuilder();
        foreach (var f in stack)
        {
            if (f.Segment == "[]")
            {
                sb.Append("[]");
            }
            else
            {
                if (sb.Length > 0 && sb[^1] != ']') sb.Append('.');
                else if (sb.Length > 0 && sb[^1] == ']') sb.Append('.');
                sb.Append(f.Segment);
            }
        }
        return sb.ToString();
    }

    // Splits "key: value" into key and (unquoted) value. Returns false if there is no
    // mapping colon (a colon followed by a space, or a trailing colon).
    private static bool TrySplitKeyValue(string s, out string key, out string value)
    {
        key = "";
        value = "";
        int colon = FindMappingColon(s);
        if (colon < 0) return false;

        key = Unquote(s[..colon].TrimEnd(' '));
        if (key.Length == 0) return false;
        var rest = s[(colon + 1)..].Trim(' ');
        value = Unquote(rest);
        return true;
    }

    // Finds the index of the colon that separates a mapping key from its value:
    // a ':' that is at end-of-string or followed by whitespace, ignoring colons inside quotes.
    private static int FindMappingColon(string s)
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
            if (c == ':' && (i + 1 == s.Length || s[i + 1] == ' '))
                return i;
        }
        return -1;
    }

    // If the value introduces a block scalar ("|" or ">"), skip its indented content lines
    // so they are not parsed as entries. Returns the index of the last consumed line.
    private static int SkipBlockScalar(string[] lines, int index, int keyIndent, string value)
    {
        var v = value.TrimEnd(' ');
        if (v.Length == 0) return index;
        char c0 = v[0];
        if (c0 != '|' && c0 != '>') return index;
        // Remaining chars (if any) must be block indicators (+, -, digits) only.
        for (int k = 1; k < v.Length; k++)
            if (v[k] != '+' && v[k] != '-' && !char.IsDigit(v[k])) return index;

        int j = index + 1;
        for (; j < lines.Length; j++)
        {
            var raw = lines[j].TrimEnd('\r');
            if (raw.Trim().Length == 0) continue; // blank lines belong to the scalar
            int ind = raw.Length - raw.TrimStart(' ').Length;
            if (ind <= keyIndent) break;
        }
        return j - 1;
    }

    // Strips an inline "# comment" that is preceded by whitespace and outside quotes.
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
            if (c == '#' && (i == 0 || s[i - 1] == ' ' || s[i - 1] == '\t'))
                return s[..i].TrimEnd(' ');
        }
        return s;
    }

    // Removes surrounding single/double quotes from a scalar, if present.
    private static string Unquote(string s)
    {
        if (s.Length >= 2)
        {
            char a = s[0], b = s[^1];
            if ((a == '"' && b == '"') || (a == '\'' && b == '\''))
                return s[1..^1];
        }
        return s;
    }
}
