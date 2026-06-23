using System.Globalization;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace Cop.Cli.Commands;

/// <summary>
/// Shows two interactive, non-blocking notices on cop startup:
///  1. A once-a-day check for a newer release, with a persistent yellow reminder to run
///     `cop update` (the network check is throttled to at most once per day; the reminder shows
///     every run until the user is up to date, using the cached latest tag).
///  2. After an update, a concise "what's new" summary covering every version newer than the one
///     the user last ran (so skipped versions are included). Only <c>approved</c> release notes
///     are shown.
///
/// Everything is fail-silent and only runs when stderr is an interactive terminal, so it never
/// blocks, breaks, or pollutes scripted/CI output.
/// </summary>
internal static class VersionNotifier
{
    private const string RepoOwner = "KrzysztofCwalina";
    private const string RepoName = "cop";
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    internal sealed record ReleaseNote(string Version, bool Approved, string[] Features);
    internal sealed record State(DateTime? LastCheck, string? LatestTag, string? SeenVersion);
    internal sealed record Message(ConsoleColor Color, string Text);

    /// <summary>Entry point — call once at startup. Never throws.</summary>
    public static void Notify()
    {
        try
        {
            // Interactive only: skip in pipes/redirects/CI so we never pollute captured output.
            if (Console.IsErrorRedirected) return;
            var current = Assembly.GetExecutingAssembly().GetName().Version;
            if (current is null) return;

            var path = StatePath();
            var (newState, messages) = Run(ReadState(path), LoadReleaseNotes(), current, DateTime.UtcNow, TryFetchLatestTag);
            foreach (var m in messages) WriteColored(m.Color, m.Text);
            WriteState(path, newState);
        }
        catch
        {
            // A version notice must never break cop.
        }
    }

    /// <summary>
    /// Pure orchestration (unit-tested): given the persisted state, the release notes, the
    /// running version and "now", produces the messages to show and the next state. The only
    /// impure input is <paramref name="fetchLatest"/> (the network call), which is injected.
    /// </summary>
    internal static (State NewState, List<Message> Messages) Run(
        State state, IReadOnlyList<ReleaseNote> notes, Version current, DateTime now, Func<string?> fetchLatest)
    {
        var messages = new List<Message>();

        // 1. What's new since the version the user last ran (includes skipped versions).
        var seen = ParseVersion(state.SeenVersion);
        if (seen is not null && current > seen)
        {
            var newNotes = SelectNewFeatures(notes, seen, current);
            if (newNotes.Count > 0)
            {
                messages.Add(new Message(ConsoleColor.Cyan, $"\u2728 cop updated to {current}. What's new:"));
                foreach (var note in newNotes)
                    foreach (var feature in note.Features)
                        messages.Add(new Message(ConsoleColor.Cyan, $"   \u2022 {feature}"));
                messages.Add(new Message(ConsoleColor.Cyan, ""));
            }
        }
        var newSeen = seen is null || current > seen ? current.ToString() : state.SeenVersion;

        // 2. Throttled remote check + persistent "update available" reminder.
        var lastCheck = state.LastCheck;
        var latestTag = state.LatestTag;
        if (ShouldCheckRemote(lastCheck, now))
        {
            var fetched = fetchLatest();
            lastCheck = now;                       // throttle even on failure
            if (fetched is not null) latestTag = fetched;
        }
        if (IsUpdateAvailable(latestTag, current))
            messages.Add(new Message(ConsoleColor.Yellow,
                $"A new version of cop is available ({latestTag}). Run 'cop update' to upgrade."));

        return (new State(lastCheck, latestTag, newSeen), messages);
    }

    // ── Pure logic (unit-tested) ─────────────────────────────────────────────

    /// <summary>Approved release notes for versions in (seen, current], oldest first.</summary>
    internal static List<ReleaseNote> SelectNewFeatures(IEnumerable<ReleaseNote> notes, Version seen, Version current)
        => notes
            .Select(n => (Note: n, Ver: ParseVersion(n.Version)))
            .Where(x => x.Ver is not null && x.Ver > seen && x.Ver <= current && x.Note.Approved && x.Note.Features.Length > 0)
            .OrderBy(x => x.Ver)
            .Select(x => x.Note)
            .ToList();

    /// <summary>True when no check has happened yet or the last one was at least a day ago.</summary>
    internal static bool ShouldCheckRemote(DateTime? lastCheck, DateTime now)
        => lastCheck is null || now - lastCheck.Value >= CheckInterval;

    /// <summary>True when the cached latest tag is a strictly newer version than the current build.</summary>
    internal static bool IsUpdateAvailable(string? latestTag, Version current)
        => ParseVersion(latestTag) is { } v && v > current;

    /// <summary>Parses a version string or "v"-prefixed tag (e.g. "v2026.6.22.4"). Null if invalid.</summary>
    internal static Version? ParseVersion(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        if (s.Length > 0 && (s[0] == 'v' || s[0] == 'V')) s = s[1..];
        return Version.TryParse(s, out var v) ? v : null;
    }

    // ── Output ───────────────────────────────────────────────────────────────

    private static void WriteColored(ConsoleColor color, string text)
    {
        if (ConsoleMarkdown.NoColor)
        {
            Console.Error.WriteLine(text);
            return;
        }
        var prev = Console.ForegroundColor;
        try
        {
            Console.ForegroundColor = color;
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
            if (!File.Exists(path)) return new State(null, null, null);
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            DateTime? last = null;
            if (root.TryGetProperty("lastCheck", out var lc) && lc.ValueKind == JsonValueKind.String &&
                DateTime.TryParse(lc.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d))
                last = d.ToUniversalTime();
            var tag = root.TryGetProperty("latestTag", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            var seen = root.TryGetProperty("seenVersion", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
            return new State(last, tag, seen);
        }
        catch
        {
            return new State(null, null, null);
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
                seenVersion = state.SeenVersion,
            };
            File.WriteAllText(path, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // best-effort
        }
    }

    internal static List<ReleaseNote> LoadReleaseNotes()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Cop.Cli.ReleaseNotes.json");
            if (stream is null) return [];
            using var doc = JsonDocument.Parse(stream);
            var result = new List<ReleaseNote>();
            if (doc.RootElement.TryGetProperty("releases", out var releases) && releases.ValueKind == JsonValueKind.Array)
            {
                foreach (var r in releases.EnumerateArray())
                {
                    if (!r.TryGetProperty("version", out var v) || v.GetString() is not { } version) continue;
                    var approved = r.TryGetProperty("approved", out var a) && a.ValueKind == JsonValueKind.True;
                    var features = new List<string>();
                    if (r.TryGetProperty("features", out var fs) && fs.ValueKind == JsonValueKind.Array)
                        foreach (var f in fs.EnumerateArray())
                            if (f.GetString() is { } feature) features.Add(feature);
                    result.Add(new ReleaseNote(version, approved, features.ToArray()));
                }
            }
            return result;
        }
        catch
        {
            return [];
        }
    }

    private static string? TryFetchLatestTag()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("cop-version-notifier/1.0");
            var url = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";
            using var resp = http.GetAsync(url).GetAwaiter().GetResult();
            if (!resp.IsSuccessStatusCode) return null;
            var json = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
        }
        catch
        {
            return null;
        }
    }
}
