using System.Text;

namespace Cop.Providers.Sql;

/// <summary>
/// A small, dependency-free SQL parser focused on static-analysis queries. It splits
/// statements on top-level semicolons while ignoring quotes and SQL comments.
/// </summary>
public static class SqlParser
{
    public record SqlParseResult(List<SqlStatementInfo> Statements);

    private static readonly HashSet<string> KnownKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "INSERT", "UPDATE", "DELETE", "CREATE", "ALTER", "DROP", "MERGE"
    };

    public static SqlParseResult Parse(string text)
    {
        var statements = new List<SqlStatementInfo>();
        int statementStart = 0;
        int line = 1;

        char quote = '\0';
        bool inLineComment = false;
        bool inBlockComment = false;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            char next = i + 1 < text.Length ? text[i + 1] : '\0';

            if (inLineComment)
            {
                if (c == '\n')
                {
                    inLineComment = false;
                    line++;
                }
                continue;
            }

            if (inBlockComment)
            {
                if (c == '\n')
                    line++;
                else if (c == '*' && next == '/')
                {
                    inBlockComment = false;
                    i++;
                }
                continue;
            }

            if (quote != '\0')
            {
                if (c == '\n')
                    line++;
                else if (c == quote)
                {
                    if (quote == '\'' && next == '\'')
                    {
                        i++;
                    }
                    else
                    {
                        quote = '\0';
                    }
                }
                continue;
            }

            if (c == '-' && next == '-')
            {
                inLineComment = true;
                i++;
                continue;
            }

            if (c == '/' && next == '*')
            {
                inBlockComment = true;
                i++;
                continue;
            }

            if (c == '\'' || c == '"')
            {
                quote = c;
                continue;
            }

            if (c == ';')
            {
                AddStatement(statements, text, statementStart, i);
                statementStart = i + 1;
                continue;
            }

            if (c == '\n')
                line++;
        }

        AddStatement(statements, text, statementStart, text.Length);
        return new SqlParseResult(statements);
    }

    private static void AddStatement(List<SqlStatementInfo> statements, string text, int start, int end)
    {
        if (end <= start)
            return;

        var raw = text[start..end];
        var analysisText = RemoveCommentsPreserveWhitespace(raw);
        var normalized = Normalize(raw);
        if (normalized.Length == 0 || analysisText.Trim().Length == 0)
            return;

        var kind = GetKind(analysisText);
        var line = 1 + CountNewLines(text, 0, start) + CountLeadingLinesBeforeCode(raw);
        bool selectsStar = kind == "SELECT" && SelectsStar(analysisText);
        bool hasWhere = HasTopLevelKeyword(analysisText, "WHERE");

        statements.Add(new SqlStatementInfo(kind, normalized, line, selectsStar, hasWhere));
    }

    private static string GetKind(string text)
    {
        int i = 0;
        while (i < text.Length && char.IsWhiteSpace(text[i]))
            i++;

        int start = i;
        while (i < text.Length && (char.IsLetter(text[i]) || text[i] == '_'))
            i++;

        if (i == start)
            return "OTHER";

        var keyword = text[start..i].ToUpperInvariant();
        return KnownKinds.Contains(keyword) ? keyword : "OTHER";
    }

    private static bool SelectsStar(string text)
    {
        int start = SkipWhitespace(text, 0);
        if (!TryReadKeyword(text, start, "SELECT", out int index))
            return false;

        index = SkipWhitespace(text, index);
        if (TryReadKeyword(text, index, "DISTINCT", out int afterDistinct)
            || TryReadKeyword(text, index, "ALL", out afterDistinct))
        {
            index = SkipWhitespace(text, afterDistinct);
        }

        int from = FindTopLevelKeyword(text, "FROM", index);
        if (from < 0)
            return false;

        var selectList = text[index..from].Trim();
        return selectList == "*";
    }

    private static bool HasTopLevelKeyword(string text, string keyword)
        => FindTopLevelKeyword(text, keyword, 0) >= 0;

    private static int FindTopLevelKeyword(string text, string keyword, int startIndex)
    {
        char quote = '\0';
        int depth = 0;

        for (int i = startIndex; i < text.Length; i++)
        {
            char c = text[i];
            char next = i + 1 < text.Length ? text[i + 1] : '\0';

            if (quote != '\0')
            {
                if (c == quote)
                {
                    if (quote == '\'' && next == '\'')
                    {
                        i++;
                    }
                    else
                    {
                        quote = '\0';
                    }
                }
                continue;
            }

            if (c == '\'' || c == '"')
            {
                quote = c;
                continue;
            }

            if (c == '(')
            {
                depth++;
                continue;
            }
            if (c == ')' && depth > 0)
            {
                depth--;
                continue;
            }

            if (depth == 0 && IsKeywordAt(text, i, keyword))
                return i;
        }

        return -1;
    }

    private static bool TryReadKeyword(string text, int index, string keyword, out int nextIndex)
    {
        nextIndex = index;
        if (!IsKeywordAt(text, index, keyword))
            return false;

        nextIndex = index + keyword.Length;
        return true;
    }

    private static bool IsKeywordAt(string text, int index, string keyword)
    {
        if (index < 0 || index + keyword.Length > text.Length)
            return false;

        if (!text.AsSpan(index, keyword.Length).Equals(keyword.AsSpan(), StringComparison.OrdinalIgnoreCase))
            return false;

        bool before = index == 0 || !IsIdentifierChar(text[index - 1]);
        bool after = index + keyword.Length == text.Length || !IsIdentifierChar(text[index + keyword.Length]);
        return before && after;
    }

    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static int SkipWhitespace(string text, int index)
    {
        while (index < text.Length && char.IsWhiteSpace(text[index]))
            index++;
        return index;
    }

    private static string RemoveCommentsPreserveWhitespace(string text)
    {
        var sb = new StringBuilder(text.Length);
        char quote = '\0';
        bool inLineComment = false;
        bool inBlockComment = false;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            char next = i + 1 < text.Length ? text[i + 1] : '\0';

            if (inLineComment)
            {
                if (c == '\n')
                {
                    inLineComment = false;
                    sb.Append(c);
                }
                else
                {
                    sb.Append(' ');
                }
                continue;
            }

            if (inBlockComment)
            {
                if (c == '*' && next == '/')
                {
                    sb.Append("  ");
                    inBlockComment = false;
                    i++;
                }
                else
                {
                    sb.Append(c == '\n' ? c : ' ');
                }
                continue;
            }

            if (quote != '\0')
            {
                sb.Append(c);
                if (c == quote)
                {
                    if (quote == '\'' && next == '\'')
                    {
                        sb.Append(next);
                        i++;
                    }
                    else
                    {
                        quote = '\0';
                    }
                }
                continue;
            }

            if (c == '-' && next == '-')
            {
                sb.Append("  ");
                inLineComment = true;
                i++;
                continue;
            }

            if (c == '/' && next == '*')
            {
                sb.Append("  ");
                inBlockComment = true;
                i++;
                continue;
            }

            if (c == '\'' || c == '"')
                quote = c;

            sb.Append(c);
        }

        return sb.ToString();
    }

    private static string Normalize(string text)
    {
        var sb = new StringBuilder();
        bool pendingSpace = false;

        foreach (char c in text.Trim())
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = true;
                continue;
            }

            if (pendingSpace && sb.Length > 0)
                sb.Append(' ');
            sb.Append(c);
            pendingSpace = false;
        }

        return sb.ToString();
    }

    private static int CountLeadingLinesBeforeCode(string raw)
    {
        var stripped = RemoveCommentsPreserveWhitespace(raw);
        for (int i = 0; i < stripped.Length; i++)
        {
            if (!char.IsWhiteSpace(stripped[i]))
                return CountNewLines(stripped, 0, i);
        }
        return 0;
    }

    private static int CountNewLines(string text, int start, int end)
    {
        int count = 0;
        for (int i = start; i < end && i < text.Length; i++)
        {
            if (text[i] == '\n')
                count++;
        }
        return count;
    }
}
