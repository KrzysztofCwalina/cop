namespace Cop.Lang;

/// <summary>
/// Parses template strings into a list of literal and expression segments.
/// - {expr} for interpolation
/// - {expr@style} for styled interpolation
/// - {literal text@style} for styled literal text
/// - {{ for literal {
/// - }} for literal }
/// </summary>
public static class TemplateParser
{
    /// <summary>
    /// Parse a template string into segments.
    /// {expr} → Expression interpolation
    /// {expr@style} → Styled expression (style is kebab-case: red-bold)
    /// {literal@style} → Styled literal text (content doesn't parse as property path)
    /// {{ → literal {
    /// }} → literal }
    /// </summary>
    public static List<TemplateSegment> Parse(string template)
    {
        var segments = new List<TemplateSegment>();
        int pos = 0;
        var literal = new System.Text.StringBuilder();

        while (pos < template.Length)
        {
            if (template[pos] == '{')
            {
                // {{ → literal {
                if (pos + 1 < template.Length && template[pos + 1] == '{')
                {
                    literal.Append('{');
                    pos += 2;
                    continue;
                }

                // Try to parse {content} or {content@style}
                int? result = TryParseBlock(template, pos, out var segment);
                if (result.HasValue)
                {
                    if (literal.Length > 0)
                    {
                        segments.Add(new LiteralSegment(literal.ToString()));
                        literal.Clear();
                    }
                    segments.Add(segment!);
                    pos = result.Value;
                    continue;
                }

                // Not a valid block — treat { as literal
            }
            else if (template[pos] == '}')
            {
                // }} → literal }
                if (pos + 1 < template.Length && template[pos + 1] == '}')
                {
                    literal.Append('}');
                    pos += 2;
                    continue;
                }
            }

            literal.Append(template[pos]);
            pos++;
        }

        if (literal.Length > 0)
            segments.Add(new LiteralSegment(literal.ToString()));

        return segments;
    }

    /// <summary>
    /// Tries to parse a {content} or {content@style} block starting at pos (which points to '{').
    /// Returns the position after the closing '}', or null if invalid.
    /// </summary>
    private static int? TryParseBlock(string template, int pos, out TemplateSegment? segment)
    {
        segment = null;
        int start = pos + 1; // skip opening {

        // Find the closing } (no nesting support — braces in content must use {{ }})
        int closePos = template.IndexOf('}', start);
        if (closePos < 0) return null;

        var content = template[start..closePos];
        if (content.Length == 0) return null; // empty {} not valid

        // Check for @style suffix
        int atPos = content.IndexOf('@');
        if (atPos >= 0)
        {
            var beforeAt = content[..atPos];
            var style = content[(atPos + 1)..];

            if (style.Length == 0) return null; // trailing @ with no style

            // Try to parse content before @ as a property path
            int? pathEnd = TryParsePropertyPath(beforeAt, 0);
            if (pathEnd.HasValue && pathEnd.Value == beforeAt.Length)
            {
                // Valid property path → styled expression
                var parts = beforeAt.Split('.');
                segment = new ExpressionSegment(parts, style);
            }
            else
            {
                // Not a valid property path → styled literal text
                segment = new AnnotatedLiteralSegment(beforeAt, style);
            }
        }
        else
        {
            // No @style — try as property path for plain interpolation
            int? pathEnd = TryParsePropertyPath(content, 0);
            if (pathEnd.HasValue && pathEnd.Value == content.Length)
            {
                var parts = content.Split('.');
                segment = new ExpressionSegment(parts);
            }
            else
            {
                return null; // Not a valid expression and no style — not a template block
            }
        }

        return closePos + 1; // position after closing }
    }

    /// <summary>
    /// Tries to parse a property path (Identifier(.Identifier)*) starting at the given position.
    /// Returns the position after the last character, or null if invalid.
    /// </summary>
    private static int? TryParsePropertyPath(string template, int start)
    {
        int pos = start;
        if (pos >= template.Length || !(char.IsLetter(template[pos]) || template[pos] == '_'))
            return null;

        while (pos < template.Length && (char.IsLetterOrDigit(template[pos]) || template[pos] == '_'))
            pos++;

        while (pos < template.Length && template[pos] == '.')
        {
            pos++;
            if (pos >= template.Length || !(char.IsLetter(template[pos]) || template[pos] == '_'))
                return null;
            while (pos < template.Length && (char.IsLetterOrDigit(template[pos]) || template[pos] == '_'))
                pos++;
        }

        return pos;
    }
}
