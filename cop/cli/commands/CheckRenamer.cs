// Renames Cop identifiers in source text for `cop checks rename`.

using System.Text;
using Cop.Lang;

namespace Cop.Cli.Commands;

/// <summary>
/// Renames an identifier across Cop source text without touching comments, verbatim strings,
/// literal string text, or member names (<c>x.name</c>, <c>pkg::name</c>).
/// </summary>
internal static class CheckRenamer
{
    private static bool IsValidIdentifier(string name) =>
        name.Length > 0
        && (char.IsAsciiLetter(name[0]) || name[0] == '_')
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_' || c == '-');

    /// <summary>Returns an error message when <paramref name="name"/> cannot be used as a declaration name.</summary>
    public static string? ValidateName(string name)
    {
        if (!IsValidIdentifier(name) || name.EndsWith('-'))
        {
            return $"'{name}' is not a valid Cop name (use letters, digits, '_' and '-', starting with a letter or '_').";
        }

        return Tokenizer.Keywords.ContainsKey(name) ? $"'{name}' is a reserved Cop keyword." : null;
    }

    /// <summary>Replaces every reference to <paramref name="oldName"/> in <paramref name="source"/>.</summary>
    public static string Rename(string source, string oldName, string newName, out int count)
    {
        StringBuilder result = new(source.Length);
        int replaced = 0;
        int i = 0;
        while (i < source.Length)
        {
            char c = source[i];
            if (c == '#')
            {
                int end = source.IndexOf('\n', i);
                end = end < 0 ? source.Length : end;
                result.Append(source, i, end - i);
                i = end;
            }
            else if (c == '@' && i + 1 < source.Length && source[i + 1] == '\'')
            {
                int end = i + 2;
                while (end < source.Length)
                {
                    if (source[end] == '\'')
                    {
                        if (end + 1 < source.Length && source[end + 1] == '\'')
                        {
                            end += 2;
                            continue;
                        }
                        end++;
                        break;
                    }
                    end++;
                }
                result.Append(source, i, end - i);
                i = end;
            }
            else if (c == '\'')
            {
                bool triple = i + 2 < source.Length && source[i + 1] == '\'' && source[i + 2] == '\'';
                int contentStart = i + (triple ? 3 : 1);
                int end = contentStart;
                while (end < source.Length)
                {
                    if (triple)
                    {
                        if (end + 2 < source.Length && source[end] == '\'' && source[end + 1] == '\'' && source[end + 2] == '\'')
                        {
                            break;
                        }
                    }
                    else if (source[end] == '\\' && end + 1 < source.Length)
                    {
                        end += 2;
                        continue;
                    }
                    else if (source[end] == '\'')
                    {
                        break;
                    }
                    end++;
                }

                int closeLength = triple ? 3 : 1;
                int contentEnd = Math.Min(end, source.Length);
                result.Append(source, i, contentStart - i);
                result.Append(RenameInterpolations(source[contentStart..contentEnd], oldName, newName, ref replaced));
                int closeEnd = Math.Min(source.Length, contentEnd + closeLength);
                result.Append(source, contentEnd, closeEnd - contentEnd);
                i = closeEnd;
            }
            else if (char.IsDigit(c))
            {
                int end = i;
                while (end < source.Length && (char.IsLetterOrDigit(source[end]) || source[end] == '.'))
                {
                    end++;
                }
                result.Append(source, i, end - i);
                i = end;
            }
            else if (char.IsLetter(c) || c == '_')
            {
                int end = ReadIdentifierEnd(source, i);
                string identifier = source[i..end];
                if (identifier == oldName && !IsMemberName(source, i))
                {
                    result.Append(newName);
                    replaced++;
                }
                else
                {
                    result.Append(identifier);
                }
                i = end;
            }
            else
            {
                result.Append(c);
                i++;
            }
        }

        count = replaced;
        return result.ToString();
    }

    private static string RenameInterpolations(string raw, string oldName, string newName, ref int replaced)
    {
        StringBuilder result = new(raw.Length);
        int i = 0;
        while (i < raw.Length)
        {
            int open = raw.IndexOf('{', i);
            int close = open < 0 ? -1 : raw.IndexOf('}', open + 1);
            if (close < 0)
            {
                result.Append(raw, i, raw.Length - i);
                break;
            }

            result.Append(raw, i, open + 1 - i);
            string inner = raw[(open + 1)..close];
            int at = inner.IndexOf('@');
            string expression = at < 0 ? inner : inner[..at];
            bool isStyledText = at >= 0 && expression.Length > 0 && expression.All(ch => char.IsLetterOrDigit(ch) || ch == '_');
            if (isStyledText)
            {
                result.Append(inner);
            }
            else
            {
                result.Append(Rename(expression, oldName, newName, out int count));
                replaced += count;
                if (at >= 0)
                {
                    result.Append(inner, at, inner.Length - at);
                }
            }

            result.Append('}');
            i = close + 1;
        }

        return result.ToString();
    }

    private static int ReadIdentifierEnd(string source, int start)
    {
        int end = start;
        while (end < source.Length && (char.IsLetterOrDigit(source[end]) || source[end] == '_' || source[end] == '-'))
        {
            end++;
        }
        return end;
    }

    private static bool IsMemberName(string source, int start)
    {
        int previous = start - 1;
        while (previous >= 0 && char.IsWhiteSpace(source[previous]))
        {
            previous--;
        }

        return previous >= 0 && (source[previous] == '.' ||
            (source[previous] == ':' && previous > 0 && source[previous - 1] == ':'));
    }
}
