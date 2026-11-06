// Reuses compiled regular expressions within a bounded, thread-safe cache.

using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Cop.Core;

/// <summary>
/// Thread-safe cache for compiled Regex instances.
/// Avoids recompiling the same pattern on every call to matches(), glob, etc.
/// Bounded to prevent unbounded memory growth in long-lived processes.
/// </summary>
internal static class RegexCache
{
    private const int MaxEntries = 1024;
    private static readonly ConcurrentDictionary<(string Pattern, RegexOptions Options), Regex> s_cache = new();

    /// <summary>
    /// Gets or creates a Regex for the given pattern and options.
    /// </summary>
    public static Regex Get(string pattern, RegexOptions options = RegexOptions.None)
    {
        (string pattern, RegexOptions options) key = (pattern, options);
        if (s_cache.TryGetValue(key, out Regex? cached))
        {
            return cached;
        }

        // Evict all if over capacity (simple bounded strategy)
        if (s_cache.Count >= MaxEntries)
        {
            s_cache.Clear();
        }

        Regex regex = new Regex(pattern, options);
        s_cache.TryAdd(key, regex);
        return regex;
    }

    /// <summary>
    /// Tests whether input matches the pattern, using cached regex.
    /// </summary>
    public static bool IsMatch(string input, string pattern, RegexOptions options = RegexOptions.None)
        => Get(pattern, options).IsMatch(input);
}
