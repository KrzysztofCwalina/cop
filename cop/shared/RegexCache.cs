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
    private static readonly ConcurrentDictionary<(string Pattern, RegexOptions Options), Regex> _cache = new();

    /// <summary>
    /// Gets or creates a Regex for the given pattern and options.
    /// </summary>
    public static Regex Get(string pattern, RegexOptions options = RegexOptions.None)
    {
        var key = (pattern, options);
        if (_cache.TryGetValue(key, out var cached))
            return cached;

        // Evict all if over capacity (simple bounded strategy)
        if (_cache.Count >= MaxEntries)
            _cache.Clear();

        var regex = new Regex(pattern, options);
        _cache.TryAdd(key, regex);
        return regex;
    }

    /// <summary>
    /// Tests whether input matches the pattern, using cached regex.
    /// </summary>
    public static bool IsMatch(string input, string pattern, RegexOptions options = RegexOptions.None)
        => Get(pattern, options).IsMatch(input);
}
