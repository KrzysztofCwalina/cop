using System.Globalization;
using System.Text;
using NUnit.Framework;

namespace Cop.Tests;

internal static class TestTextParsing
{
    internal static void AssertExecutionSummary(string stderr, int files, int commands)
    {
        var line = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.TrimEnd('\r')).Single(value => value.StartsWith("  Summary   ", StringComparison.Ordinal));
        var prefix = $"  Summary   {files} input file{(files == 1 ? "" : "s")} processed, " +
            $"{commands} computation{(commands == 1 ? "" : "s")} evaluated in ";
        Assert.That(line, Does.StartWith(prefix).And.EndWith("s."));
        Assert.That(double.TryParse(line[prefix.Length..^2], NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out var seconds), Is.True);
        Assert.That(seconds, Is.InRange(0, 30));
    }

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
