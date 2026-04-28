namespace Cop.Lang;

/// <summary>
/// A structured string composed of text spans with optional style annotations.
/// Produced by template resolution and consumed by output renderers.
/// </summary>
public class RichString
{
    public IReadOnlyList<TextSpan> Spans { get; }

    public RichString(IReadOnlyList<TextSpan> spans) => Spans = spans;

    public RichString(string plainText) => Spans = [new TextSpan(plainText)];

    /// <summary>
    /// Returns the plain text content with all annotations stripped.
    /// </summary>
    public string ToPlainText()
    {
        if (Spans.Count == 0) return "";
        if (Spans.Count == 1) return Spans[0].Text;
        var sb = new System.Text.StringBuilder();
        foreach (var span in Spans)
            sb.Append(span.Text);
        return sb.ToString();
    }

    /// <summary>
    /// Parses annotation shorthand (e.g., "red", "color=red,weight=bold") into key-value pairs.
    /// Bare words are mapped to "color" by default.
    /// </summary>
    public static IReadOnlyDictionary<string, string>? ParseAnnotation(string? annotation)
    {
        if (string.IsNullOrEmpty(annotation)) return null;

        var result = new Dictionary<string, string>();
        foreach (var part in annotation.Split('-'))
        {
            var trimmed = part.Trim();
            if (trimmed.Length == 0) continue;
            var eq = trimmed.IndexOf('=');
            if (eq >= 0)
            {
                result[trimmed[..eq].Trim()] = trimmed[(eq + 1)..].Trim();
            }
            else
            {
                // Shorthand: bare word maps to "color" unless it's a known style key
                if (trimmed is "bold" or "italic" or "underline" or "dim" or "strikethrough")
                    result["weight"] = trimmed;
                else
                    result["color"] = trimmed;
            }
        }
        return result.Count > 0 ? result : null;
    }

    public override string ToString() => ToPlainText();
}
