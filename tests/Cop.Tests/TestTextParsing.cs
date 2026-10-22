using System.Globalization;
using System.Text;

namespace Cop.Tests;

internal static class TestTextParsing
{
    internal static bool IsWordCharacter(char value) =>
        char.IsLetterOrDigit(value)
        || char.GetUnicodeCategory(value) is UnicodeCategory.NonSpacingMark or UnicodeCategory.ConnectorPunctuation;

    internal static string StripAnsi(string text, bool colorsOnly = false)
    {
        var result = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\x1b' && i + 1 < text.Length && text[i + 1] == '[')
            {
                var end = i + 2;
                if (colorsOnly)
                {
                    while (end < text.Length && (char.IsAsciiDigit(text[end]) || text[end] == ';'))
                        end++;
                }
                else
                {
                    while (end < text.Length && text[end] is >= '0' and <= '?')
                        end++;
                    while (end < text.Length && text[end] is >= ' ' and <= '/')
                        end++;
                }

                if (end < text.Length && (colorsOnly ? text[end] == 'm' : text[end] is >= '@' and <= '~'))
                {
                    i = end;
                    continue;
                }
            }

            result.Append(text[i]);
        }
        return result.ToString();
    }
}
