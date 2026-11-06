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
/// It only runs when stderr is an interactive terminal. Expected network and filesystem failures
/// are reported diagnostically; programming errors propagate. cop deliberately does NOT print a post-update
/// "what's new" summary — release notes live on the GitHub releases page.
/// </summary>
internal static class VersionNotifier
{
    private const string RepoOwner = "KrzysztofCwalina";
    private const string RepoName = "cop";
    private static readonly TimeSpan s_checkInterval = TimeSpan.FromHours(24);

    internal sealed record State(DateTime? LastCheck, string? LatestTag);
    internal sealed record Message(ConsoleColor? Color, string Text);

    /// <summary>Checks for updates without blocking on asynchronous network operations.</summary>
    public static async Task NotifyAsync()
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
            State state = ReadState(path);
            DateTime now = DateTime.UtcNow;
            string? fetched = ShouldCheckRemote(state.LastCheck, now)
                ? await TryFetchLatestTagAsync()
                : null;
            (State newState, List<Message> messages) = Run(state, current, now, () => fetched);
            foreach (Message m in messages)
            {
                WriteColored(m.Color, m.Text);
            }

            WriteState(path, newState);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            System.Security.SecurityException or NotSupportedException)
        {
            ReportFailure("display update notice", ex);
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
        => lastCheck is null || now - lastCheck.Value >= s_checkInterval;

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
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("Update-check state must be a JSON object.");
            }
            DateTime? last = null;
            if (root.TryGetProperty("lastCheck", out JsonElement lc) && lc.ValueKind == JsonValueKind.String &&
                DateTime.TryParse(lc.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime d))
            {
                last = d.ToUniversalTime();
            }

            string? tag = root.TryGetProperty("latestTag", out JsonElement t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            return new State(last, tag);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            JsonException or System.Security.SecurityException)
        {
            ReportFailure("read update-check state", ex);
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            System.Security.SecurityException)
        {
            ReportFailure("write update-check state", ex);
        }
    }

    private static async Task<string?> TryFetchLatestTagAsync()
    {
        try
        {
            using HttpClient http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("cop-version-notifier/1.0");
            string url = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";
            using HttpResponseMessage resp = await http.GetAsync(url);
            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }

            string json = await resp.Content.ReadAsStringAsync();
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("tag_name", out JsonElement tag) ||
                tag.ValueKind != JsonValueKind.String)
            {
                throw new JsonException("The latest release has no string tag_name.");
            }
            return tag.GetString();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or
            JsonException or IOException)
        {
            ReportFailure("fetch latest release", ex);
            return null;
        }
    }

    private static void ReportFailure(string operation, Exception error)
    {
        if (Cop.Core.CopDiagnostics.Level > 0)
        {
            Console.Error.WriteLine($"[diag] Unable to {operation}: {error.Message}");
        }
    }
}
