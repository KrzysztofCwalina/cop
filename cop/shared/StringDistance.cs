namespace Cop.Lang;

/// <summary>
/// Utility for computing string edit distance and suggesting closest matches.
/// Used for "Did you mean X?" hints in error messages.
/// </summary>
public static class StringDistance
{
    /// <summary>
    /// Computes the Levenshtein edit distance between two strings (case-insensitive).
    /// </summary>
    public static int Levenshtein(string a, string b)
    {
        if (string.IsNullOrEmpty(a)) return b?.Length ?? 0;
        if (string.IsNullOrEmpty(b)) return a.Length;

        a = a.ToLowerInvariant();
        b = b.ToLowerInvariant();

        int[] prev = new int[b.Length + 1];
        int[] curr = new int[b.Length + 1];

        for (int j = 0; j <= b.Length; j++)
            prev[j] = j;

        for (int i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(
                    Math.Min(curr[j - 1] + 1, prev[j] + 1),
                    prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }

        return prev[b.Length];
    }

    /// <summary>
    /// Finds the closest match to <paramref name="input"/> from a list of candidates.
    /// Returns null if no candidate is close enough (within maxDistance).
    /// </summary>
    /// <param name="input">The unrecognized input string.</param>
    /// <param name="candidates">Known valid strings to compare against.</param>
    /// <param name="maxDistance">Maximum edit distance to consider (default: 3).</param>
    public static string? FindClosest(string input, IEnumerable<string> candidates, int maxDistance = 3)
    {
        string? best = null;
        int bestDist = int.MaxValue;

        foreach (var candidate in candidates)
        {
            // Quick reject: if lengths differ by more than maxDistance, skip
            if (Math.Abs(input.Length - candidate.Length) > maxDistance)
                continue;

            int dist = Levenshtein(input, candidate);
            if (dist < bestDist && dist <= maxDistance)
            {
                bestDist = dist;
                best = candidate;
            }
        }

        return best;
    }

    /// <summary>
    /// Finds up to <paramref name="maxResults"/> closest matches within maxDistance.
    /// Results are ordered by distance (closest first).
    /// </summary>
    public static List<string> FindClosestN(string input, IEnumerable<string> candidates, int maxResults = 3, int maxDistance = 3)
    {
        var results = new List<(string Name, int Dist)>();

        foreach (var candidate in candidates)
        {
            if (Math.Abs(input.Length - candidate.Length) > maxDistance)
                continue;

            int dist = Levenshtein(input, candidate);
            if (dist <= maxDistance)
                results.Add((candidate, dist));
        }

        results.Sort((a, b) => a.Dist.CompareTo(b.Dist));
        return results.Take(maxResults).Select(r => r.Name).ToList();
    }
}
