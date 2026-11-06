// Matches normalized paths against wildcards without compiling regular expressions.

namespace Cop.Core;

/// <summary>
/// Matches file paths with single-segment and recursive wildcard patterns.
/// </summary>
public static class PathGlob
{
    /// <summary>
    /// Matches a whole path against a glob after normalizing directory separators.
    /// </summary>
    /// <param name="path">Path to test, using either directory separator.</param>
    /// <param name="pattern">Pattern supporting ?, *, and recursive ** wildcards.</param>
    /// <param name="comparison">Ordinal literal comparisons, with or without case folding.</param>
    /// <returns>Whether the entire normalized path matches the pattern.</returns>
    public static bool IsMatch(string path, string pattern,
        StringComparison comparison = StringComparison.OrdinalIgnoreCase)
    {
        bool ignoreCase = comparison switch
        {
            StringComparison.OrdinalIgnoreCase => true,
            StringComparison.Ordinal => false,
            _ => throw new ArgumentOutOfRangeException(nameof(comparison),
                "Path globs support ordinal comparisons only.")
        };
        string normalized = path.Replace('\\', '/');
        string glob = pattern.Replace('\\', '/');
        bool[] current = new bool[normalized.Length + 1];
        bool[] next = new bool[current.Length];
        current[0] = true;
        for (int i = 0; i < glob.Length; i++)
        {
            Array.Clear(next);
            if (glob[i] == '*')
            {
                bool recursive = i + 1 < glob.Length && glob[i + 1] == '*';
                if (recursive)
                {
                    i++;
                }

                bool directories = recursive && i + 1 < glob.Length && glob[i + 1] == '/';
                if (directories)
                {
                    i++;
                }

                bool reachable = false;
                for (int position = 0; position < current.Length; position++)
                {
                    if (directories)
                    {
                        next[position] = current[position]
                            || (reachable && position > 0 && normalized[position - 1] == '/');
                    }
                    else
                    {
                        next[position] = current[position]
                            || (position > 0 && next[position - 1] && (recursive || normalized[position - 1] != '/'));
                    }

                    reachable |= current[position];
                }
            }
            else
            {
                for (int position = 1; position < current.Length; position++)
                {
                    char value = normalized[position - 1];
                    next[position] = current[position - 1] && (glob[i] == '?'
                        ? value != '/'
                        : ignoreCase
                            ? char.ToUpperInvariant(value) == char.ToUpperInvariant(glob[i])
                            : value == glob[i]);
                }
            }
            (current, next) = (next, current);
        }
        return current[normalized.Length];
    }
}
