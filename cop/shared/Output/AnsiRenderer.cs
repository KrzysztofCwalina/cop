namespace Cop.Lang;

/// <summary>
/// Renders RichString spans with ANSI escape codes for terminal color output.
/// </summary>
public static class AnsiRenderer
{
    private static readonly Dictionary<string, string> ColorCodes = new(StringComparer.OrdinalIgnoreCase)
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
        // Semantic aliases — usable in @style annotations (e.g., {text@warning-bold})
        ["error"] = "\x1b[31m",     // red
        ["warning"] = "\x1b[33m",   // yellow
        ["info"] = "\x1b[36m",      // cyan
    };

    private static readonly Dictionary<string, string> WeightCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["bold"] = "\x1b[1m",
        ["dim"] = "\x1b[2m",
        ["italic"] = "\x1b[3m",
        ["underline"] = "\x1b[4m",
        ["strikethrough"] = "\x1b[9m",
    };

    // Semantic aliases used only by the "auto" color annotation.
    // Maps common text values to ANSI color codes without polluting the main ColorCodes table.
    private static readonly Dictionary<string, string> AutoColorAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["error"] = "\x1b[31m",     // red
        ["warning"] = "\x1b[33m",   // yellow
        ["info"] = "\x1b[36m",      // cyan
    };

    private const string Reset = "\x1b[0m";

    /// <summary>
    /// Renders a RichString to a terminal string with ANSI color codes.
    /// Returns plain text if no spans have annotations.
    /// When a span has color="auto", the span's text value is used as the color lookup key,
    /// checking both named colors and semantic aliases (error→red, warning→yellow, info→cyan).
    /// </summary>
    public static string Render(RichString richString)
    {
        if (!richString.Spans.Any(s => s.HasAnnotations))
            return richString.ToPlainText();

        var sb = new System.Text.StringBuilder();
        foreach (var span in richString.Spans)
        {
            if (!span.HasAnnotations)
            {
                sb.Append(span.Text);
                continue;
            }

            bool hasCode = false;
            if (span.Annotations!.TryGetValue("color", out var color))
            {
                string? cc;
                if (string.Equals(color, "auto", StringComparison.OrdinalIgnoreCase))
                {
                    // Use the text value itself as the color key
                    if (!ColorCodes.TryGetValue(span.Text, out cc))
                        AutoColorAliases.TryGetValue(span.Text, out cc);
                }
                else
                {
                    ColorCodes.TryGetValue(color, out cc);
                }

                if (cc != null)
                {
                    sb.Append(cc);
                    hasCode = true;
                }
            }
            if (span.Annotations.TryGetValue("weight", out var weight) && WeightCodes.TryGetValue(weight, out var wc))
            {
                sb.Append(wc);
                hasCode = true;
            }

            sb.Append(span.Text);
            if (hasCode) sb.Append(Reset);
        }
        return sb.ToString();
    }
}
