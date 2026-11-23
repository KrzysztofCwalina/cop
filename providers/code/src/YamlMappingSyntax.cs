// Shares block mapping and scalar syntax between YAML-based format parsers.

namespace Cop.Providers;

/// <summary>Shared scalar mapping syntax for the YAML and OpenAPI structural parsers.</summary>
public static class YamlMappingSyntax
{
    /// <summary>Splits a block mapping at an unquoted colon followed by a space or the end of the line.</summary>
    public static bool TrySplitKeyValue(string text, out string key, out string value)
    {
        key = string.Empty;
        value = string.Empty;
        int colon = FindMappingColon(text);
        if (colon < 0)
        {
            return false;
        }
        key = Unquote(text[..colon].TrimEnd(' '));
        if (key.Length == 0)
        {
            return false;
        }
        value = Unquote(text[(colon + 1)..].Trim(' '));
        return true;
    }

    private static int FindMappingColon(string text)
    {
        char quote = '\0';
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }
                continue;
            }
            if (c == '\'' || c == '"')
            {
                quote = c;
                continue;
            }
            if (c == ':' && (i + 1 == text.Length || text[i + 1] == ' '))
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>Removes a matching pair of surrounding single or double quotes from a scalar.</summary>
    public static string Unquote(string text)
    {
        if (text.Length >= 2)
        {
            char first = text[0], last = text[^1];
            if ((first == '"' && last == '"') || (first == '\'' && last == '\''))
            {
                return text[1..^1];
            }
        }
        return text;
    }
}
