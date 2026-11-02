namespace Cop.Core;

public static class PathGlob
{
    public static bool IsMatch(string path, string pattern, bool ignoreCase = true)
    {
        var normalized = path.Replace('\\', '/');
        var glob = pattern.Replace('\\', '/');
        var current = new bool[normalized.Length + 1];
        var next = new bool[current.Length];
        current[0] = true;
        for (int i = 0; i < glob.Length; i++)
        {
            Array.Clear(next);
            if (glob[i] == '*')
            {
                bool recursive = i + 1 < glob.Length && glob[i + 1] == '*';
                if (recursive) i++;
                bool directories = recursive && i + 1 < glob.Length && glob[i + 1] == '/';
                if (directories) i++;
                bool reachable = false;
                for (int position = 0; position < current.Length; position++)
                {
                    if (directories)
                        next[position] = current[position]
                            || (reachable && position > 0 && normalized[position - 1] == '/');
                    else
                        next[position] = current[position]
                            || (position > 0 && next[position - 1] && (recursive || normalized[position - 1] != '/'));
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
