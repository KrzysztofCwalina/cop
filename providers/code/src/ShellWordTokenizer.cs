// Splits shell command segments into words for shell-language providers.

using System.Text;

namespace Cop.Providers;

/// <summary>
/// Splits a shell command segment into words, honoring single and double quotes and an escape character.
/// </summary>
public static class ShellWordTokenizer
{
    /// <summary>
    /// Splits <paramref name="segment"/> into words separated by whitespace and any of <paramref name="separators"/>.
    /// Quote characters are removed and the escape character makes the next character literal.
    /// </summary>
    /// <param name="segment">The command text to split.</param>
    /// <param name="escape">The escape character (for example '\' in Bash or '`' in PowerShell).</param>
    /// <param name="separators">Additional unquoted characters that end a word and are dropped.</param>
    /// <returns>The words in order of appearance.</returns>
    public static List<string> Tokenize(string segment, char escape, string separators = "")
    {
        List<string> tokens = new List<string>();
        StringBuilder current = new StringBuilder();
        char quote = '\0';
        bool escaped = false;

        foreach (char c in segment)
        {
            if (escaped)
            {
                current.Append(c);
                escaped = false;
                continue;
            }

            if (c == escape)
            {
                escaped = true;
                continue;
            }

            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }
                else
                {
                    current.Append(c);
                }

                continue;
            }

            if (c == '\'' || c == '"')
            {
                quote = c;
                continue;
            }

            if (char.IsWhiteSpace(c) || separators.Contains(c))
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }
}
