// Checks release versions and persists throttled update reminders for interactive users.

using System.Globalization;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace Cop.Cli.Commands;

/// <summary>
/// Shows a single interactive, non-blocking startup notice: a once-a-day check for a newer
/// release, with a persistent yellow reminder to run `cop update`. The network check is throttled
/// to at most once per day; the reminder shows every run until the user is up to date, using the
/// cached latest tag.
///
/// It is fail-silent and only runs when stderr is an interactive terminal, so it never blocks,
/// breaks, or pollutes scripted/CI output. cop deliberately does NOT print a post-update
/// "what's new" summary — release notes live on the GitHub releases page.
/// </summary>
internal static class VersionNotifier
{
    private const string RepoOwner = "KrzysztofCwalina";
    private const string RepoName = "cop";
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    internal sealed record State(DateTime? LastCheck, string? LatestTag);
    internal sealed record Message(ConsoleColor? Color, string Text);

    /// <summary>Entry point — call once at startup. Never throws.</summary>
    public static void Notify()
    {
        try
        {
            // Interactive only: skip in pipes/redirects/CI so we never pollute captured output.
            if (Console.IsErrorRedirected)
            {
                return;
            }

            Version? current = Assembly.GetExecutingAssembly().GetName().Version;
            if (current is null)
            {
                return;
            }

            string path = StatePath();
            (State? newState, List<Message>? messages) = Run(ReadState(path), current, DateTime.UtcNow, TryFetchLatestTag);
            foreach (Message m in messages)
            {
                WriteColored(m.Color, m.Text);
            }

            WriteState(path, newState);
        }
        catch
        {
            // A version notice must never break cop.
        }
    }

    /// <summary>
    /// Pure orchestration (unit-tested): given the persisted state, the running version and "now",
    /// produces the messages to show and the next state. The only impure input is
    /// <paramref name="fetchLatest"/> (the network call), which is injected.
    /// </summary>
    internal static (State NewState, List<Message> Messages) Run(
        State state, Version current, DateTime now, Func<string?> fetchLatest)
    {
        List<Message> messages = new List<Message>();

        // Throttled remote check + persistent "update available" reminder.
        DateTime? lastCheck = state.LastCheck;
        string? latestTag = state.LatestTag;
        if (ShouldCheckRemote(lastCheck, now))
        {
            string? fetched = fetchLatest();
            lastCheck = now;                       // throttle even on failure
            if (fetched is not null)
            {
                latestTag = fetched;
            }
        }
        if (IsUpdateAvailable(latestTag, current))
        {
            messages.Add(new Message(ConsoleColor.Yellow,
                $"a new version of cop is available ({latestTag}). run 'cop update' to upgrade."));
        }

        return (new State(lastCheck, latestTag), messages);
    }

    // ── Pure logic (unit-tested) ─────────────────────────────────────────────

    /// <summary>True when no check has happened yet or the last one was at least a day ago.</summary>
    internal static bool ShouldCheckRemote(DateTime? lastCheck, DateTime now)
        => lastCheck is null || now - lastCheck.Value >= CheckInterval;

    /// <summary>True when the cached latest tag is a strictly newer version than the current build.</summary>
    internal static bool IsUpdateAvailable(string? latestTag, Version current)
        => ParseVersion(latestTag) is { } v && v > current;

    /// <summary>Parses a version string or "v"-prefixed tag (e.g. "v2026.6.22.4"). Null if invalid.</summary>
    internal static Version? ParseVersion(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return null;
        }

        s = s.Trim();
        if (s.Length > 0 && (s[0] == 'v' || s[0] == 'V'))
        {
            s = s[1..];
        }

        return Version.TryParse(s, out Version? v) ? v : null;
    }

    // ── Output ───────────────────────────────────────────────────────────────

    private static void WriteColored(ConsoleColor? color, string text)
    {
        if (color is null || ConsoleMarkdown.NoColor)
        {
            Console.Error.WriteLine(text);
            return;
        }
        ConsoleColor prev = Console.ForegroundColor;
        try
        {
            Console.ForegroundColor = color.Value;
            Console.Error.WriteLine(text);
        }
        finally
        {
            Console.ForegroundColor = prev;
        }
    }

    // ── State + data ─────────────────────────────────────────────────────────

    private static string StatePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cop", "update-check.json");

    internal static State ReadState(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new State(null, null);
            }

            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = doc.RootElement;
            DateTime? last = null;
            if (root.TryGetProperty("lastCheck", out JsonElement lc) && lc.ValueKind == JsonValueKind.String &&
                DateTime.TryParse(lc.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime d))
            {
                last = d.ToUniversalTime();
            }

            string? tag = root.TryGetProperty("latestTag", out JsonElement t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            return new State(last, tag);
        }
        catch
        {
            return new State(null, null);
        }
    }

    internal static void WriteState(string path, State state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var payload = new
            {
                lastCheck = state.LastCheck?.ToString("o"),
                latestTag = state.LatestTag,
            };
            File.WriteAllText(path, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // best-effort
        }
    }

    private static string? TryFetchLatestTag()
    {
        try
        {
            using HttpClient http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("cop-version-notifier/1.0");
            string url = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";
            using HttpResponseMessage resp = http.GetAsync(url).GetAwaiter().GetResult();
            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }

            string json = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            using JsonDocument doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("tag_name", out JsonElement t) ? t.GetString() : null;
        }
        catch
        {
            return null;
        }
    }
}
