using System.Text;

namespace Cop.Providers.Sql;

/// <summary>Dependency-free SQL statement analysis, not a dialect-specific SQL compiler.</summary>
public static class SqlParser
{
    public record SqlParseResult(List<SqlStatementInfo> Statements);

    private static readonly HashSet<string> KnownKinds = new(StringComparer.OrdinalIgnoreCase)
        { "SELECT", "INSERT", "UPDATE", "DELETE", "CREATE", "ALTER", "DROP", "MERGE" };

    public static SqlParseResult Parse(string text)
    {
        var analysis = MaskQuotedTextAndComments(text);
        var statements = new List<SqlStatementInfo>();
        int start = 0, line = 1, startLine = 1;
        for (int i = 0; i <= text.Length; i++)
        {
            bool batchSeparator = i == 0 || text[i - 1] == '\n';
            int endOfLine = batchSeparator ? analysis.IndexOf('\n', i) : -1;
            if (endOfLine < 0) endOfLine = text.Length;
            batchSeparator = batchSeparator && analysis.AsSpan(i, endOfLine - i).Trim().Equals("GO", StringComparison.OrdinalIgnoreCase);
            if (i == text.Length || analysis[i] == ';' || batchSeparator)
            {
                AddStatement(statements, text[start..i], analysis[start..i], startLine);
                if (batchSeparator) i = endOfLine;
                start = i + 1;
                startLine = line + (i < text.Length && text[i] == '\n' ? 1 : 0);
            }
            if (i < text.Length && text[i] == '\n') line++;
        }
        return new(statements);
    }

    private static void AddStatement(List<SqlStatementInfo> statements, string raw, string analysis, int line)
    {
        int first = 0;
        while (first < analysis.Length && char.IsWhiteSpace(analysis[first]))
        {
            if (analysis[first] == '\n') line++;
            first++;
        }
        if (first == analysis.Length) return;
        var tokens = TopLevelWords(analysis).ToList();
        string kind = tokens.FirstOrDefault().Word ?? "OTHER";
        int operation = tokens.FirstOrDefault().Start;
        if (kind == "WITH")
        {
            var main = tokens.FirstOrDefault(t => t.Word is "SELECT" or "INSERT" or "UPDATE" or "DELETE" or "MERGE");
            kind = main.Word ?? "OTHER";
            operation = main.Start;
        }
        if (!KnownKinds.Contains(kind)) kind = "OTHER";
        int from = tokens.Where(t => t.Start > operation && t.Word == "FROM").Select(t => t.Start).DefaultIfEmpty(analysis.Length).First();
        bool star = kind == "SELECT" && ProjectionHasWildcard(analysis[(operation + 6)..from]);
        bool where = tokens.Any(t => t.Word == "WHERE" && t.Start > operation);
        statements.Add(new(kind, NormalizeWhitespace(raw), line, star, where));
    }

    private static bool ProjectionHasWildcard(string projection)
    {
        int depth = 0, start = 0;
        for (int i = 0; i <= projection.Length; i++)
        {
            char c = i == projection.Length ? ',' : projection[i];
            if (c == '(') depth++;
            else if (c == ')') depth--;
            else if (c == ',' && depth == 0)
            {
                var item = projection[start..i].Trim();
                int wordEnd = 0;
                while (wordEnd < item.Length && !char.IsWhiteSpace(item[wordEnd])) wordEnd++;
                if (wordEnd < item.Length
                    && (item.AsSpan(0, wordEnd).Equals("DISTINCT", StringComparison.OrdinalIgnoreCase)
                        || item.AsSpan(0, wordEnd).Equals("ALL", StringComparison.OrdinalIgnoreCase)))
                    item = item[wordEnd..].TrimStart();
                if (item == "*" || item.EndsWith(".*", StringComparison.Ordinal)) return true;
                start = i + 1;
            }
        }
        return false;
    }

    private static string NormalizeWhitespace(string text)
    {
        var result = new StringBuilder(text.Length);
        bool whitespace = false;
        foreach (char value in text.AsSpan().Trim())
        {
            if (char.IsWhiteSpace(value)) whitespace = true;
            else
            {
                if (whitespace && result.Length > 0) result.Append(' ');
                result.Append(value);
                whitespace = false;
            }
        }
        return result.ToString();
    }

    private static IEnumerable<(string Word, int Start)> TopLevelWords(string text)
    {
        int depth = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '(') depth++;
            else if (text[i] == ')') depth--;
            else if (depth == 0 && (char.IsLetter(text[i]) || text[i] == '_'))
            {
                int start = i;
                while (i + 1 < text.Length && (char.IsLetterOrDigit(text[i + 1]) || text[i + 1] == '_')) i++;
                yield return (text[start..(i + 1)].ToUpperInvariant(), start);
            }
        }
    }

    private static string MaskQuotedTextAndComments(string text)
    {
        var masked = text.ToCharArray();
        void Mask(int start, int end)
        {
            for (int j = start; j < end; j++)
                if (masked[j] != '\n' && masked[j] != '\r') masked[j] = ' ';
        }
        for (int i = 0; i < text.Length; i++)
        {
            int start = i;
            char c = text[i];
            if (c == '-' && i + 1 < text.Length && text[i + 1] == '-')
            {
                while (i < text.Length && text[i] != '\n') i++;
            }
            else if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                int depth = 1;
                i += 2;
                while (i < text.Length && depth > 0)
                {
                    if (i + 1 < text.Length && text[i] == '/' && text[i + 1] == '*') { depth++; i += 2; }
                    else if (i + 1 < text.Length && text[i] == '*' && text[i + 1] == '/') { depth--; i += 2; }
                    else i++;
                }
            }
            else if (c is '\'' or '"' or '`' or '[')
            {
                char closing = c == '[' ? ']' : c;
                i++;
                while (i < text.Length)
                {
                    if (text[i++] != closing) continue;
                    if (i < text.Length && text[i] == closing) { i++; continue; }
                    break;
                }
            }
            else if (c == '$')
            {
                int tagEnd = i + 1;
                while (tagEnd < text.Length && (char.IsLetterOrDigit(text[tagEnd]) || text[tagEnd] == '_')) tagEnd++;
                if (tagEnd == text.Length || text[tagEnd] != '$') continue;
                var tag = text[i..(tagEnd + 1)];
                int closing = text.IndexOf(tag, tagEnd + 1, StringComparison.Ordinal);
                i = closing < 0 ? text.Length : closing + tag.Length;
            }
            else continue;
            Mask(start, i);
            i--;
        }
        return new string(masked);
    }
}
