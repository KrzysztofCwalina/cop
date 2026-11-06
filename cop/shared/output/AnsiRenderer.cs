// Converts styled text spans into terminal escape sequences.

using System.Text;

namespace Cop.Lang;

/// <summary>
/// Renders RichString spans with ANSI escape codes for terminal color output.
/// </summary>
public static class AnsiRenderer
{
    private static readonly Dictionary<string, string> s_colorCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["black"] = "\x1b[30m",
        ["red"] = "\x1b[31m",
        ["green"] = "\x1b[32m",
        ["yellow"] = "\x1b[33m",
        ["blue"] = "\x1b[34m",
        ["magenta"] = "\x1b[35m",
        ["cyan"] = "\x1b[36m",
        ["white"] = "\x1b[37m",
        ["gray"] = "\x1b[90m",
    };

    private static readonly Dictionary<string, string> s_weightCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["bold"] = "\x1b[1m",
        ["dim"] = "\x1b[2m",
        ["italic"] = "\x1b[3m",
        ["underline"] = "\x1b[4m",
        ["strikethrough"] = "\x1b[9m",
    };

    private const string Reset = "\x1b[0m";

    /// <summary>
    /// Renders a RichString to a terminal string with ANSI color codes.
    /// Returns plain text if no spans have annotations.
    /// When a span has color="auto", the span's text value is used as the color lookup key.
    /// </summary>
    public static string Render(RichString richString)
    {
        if (!richString.Spans.Any(s => s.HasAnnotations))
        {
            return richString.ToPlainText();
        }

        StringBuilder sb = new System.Text.StringBuilder();
        foreach (TextSpan span in richString.Spans)
        {
            if (!span.HasAnnotations)
            {
                sb.Append(span.Text);
                continue;
            }

            bool hasCode = false;
            if (span.Annotations!.TryGetValue("color", out string? color))
            {
                string? cc;
                if (string.Equals(color, "auto", StringComparison.OrdinalIgnoreCase))
                {
                    // Use the text value itself as the color key
                    s_colorCodes.TryGetValue(span.Text, out cc);
                }
                else
                {
                    s_colorCodes.TryGetValue(color, out cc);
                }

                if (cc != null)
                {
                    sb.Append(cc);
                    hasCode = true;
                }
            }
            if (span.Annotations.TryGetValue("weight", out string? weight) && s_weightCodes.TryGetValue(weight, out string? wc))
            {
                sb.Append(wc);
                hasCode = true;
            }

            sb.Append(span.Text);
            if (hasCode)
            {
                sb.Append(Reset);
            }
        }
        return sb.ToString();
    }
}
