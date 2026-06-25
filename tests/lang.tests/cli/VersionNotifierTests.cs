using Cop.Cli.Commands;
using NUnit.Framework;

namespace Cop.Tests.Lang.Cli;

[TestFixture]
public class VersionNotifierTests
{
    // ── ShouldCheckRemote (daily throttle) ───────────────────────────

    [Test]
    public void ShouldCheckRemote_TrueWhenNeverChecked()
        => Assert.That(VersionNotifier.ShouldCheckRemote(null, DateTime.UtcNow), Is.True);

    [Test]
    public void ShouldCheckRemote_FalseWithinADay()
    {
        var now = DateTime.UtcNow;
        Assert.That(VersionNotifier.ShouldCheckRemote(now.AddHours(-5), now), Is.False);
    }

    [Test]
    public void ShouldCheckRemote_TrueAfterADay()
    {
        var now = DateTime.UtcNow;
        Assert.That(VersionNotifier.ShouldCheckRemote(now.AddHours(-25), now), Is.True);
    }

    // ── IsUpdateAvailable ────────────────────────────────────────────

    [Test]
    public void IsUpdateAvailable_TrueWhenTagNewer()
        => Assert.That(VersionNotifier.IsUpdateAvailable("v2026.6.22.5", new Version("2026.6.22.4")), Is.True);

    [Test]
    public void IsUpdateAvailable_FalseWhenSameOrOlderOrMissing()
    {
        var current = new Version("2026.6.22.4");
        Assert.That(VersionNotifier.IsUpdateAvailable("v2026.6.22.4", current), Is.False);
        Assert.That(VersionNotifier.IsUpdateAvailable("v2026.6.22.3", current), Is.False);
        Assert.That(VersionNotifier.IsUpdateAvailable(null, current), Is.False);
        Assert.That(VersionNotifier.IsUpdateAvailable("not-a-version", current), Is.False);
    }

    // ── ParseVersion ─────────────────────────────────────────────────

    [Test]
    public void ParseVersion_HandlesTagPrefixAndPlainAndInvalid()
    {
        Assert.That(VersionNotifier.ParseVersion("v2026.6.22.4"), Is.EqualTo(new Version("2026.6.22.4")));
        Assert.That(VersionNotifier.ParseVersion("2026.6.22.4"), Is.EqualTo(new Version("2026.6.22.4")));
        Assert.That(VersionNotifier.ParseVersion("  V2026.6.22.4  "), Is.EqualTo(new Version("2026.6.22.4")));
        Assert.That(VersionNotifier.ParseVersion("garbage"), Is.Null);
        Assert.That(VersionNotifier.ParseVersion(null), Is.Null);
    }

    // ── Run (orchestration): only the "update available" reminder ────

    [Test]
    public void Run_UpdateAvailable_EmitsYellowReminder()
    {
        var now = DateTime.UtcNow;
        var state = new VersionNotifier.State(now, "v2026.6.22.9"); // cached newer tag, recent check
        var (_, messages) = VersionNotifier.Run(state, new Version("2026.6.22.3"), now, () => null);

        var update = messages.SingleOrDefault(m => m.Color == ConsoleColor.Yellow);
        Assert.That(update, Is.Not.Null);
        Assert.That(update!.Text, Does.Contain("cop update"));
        Assert.That(update.Text, Does.Contain("2026.6.22.9"));
    }

    [Test]
    public void Run_UpToDate_EmitsNoMessages()
    {
        var now = DateTime.UtcNow;
        var state = new VersionNotifier.State(now, "v2026.6.22.3"); // on the latest, recent check
        var (_, messages) = VersionNotifier.Run(state, new Version("2026.6.22.3"), now, () => null);
        Assert.That(messages, Is.Empty);
    }

    // Regression: cop must NEVER claim "updated to cop ..." / "what's new" just because the running
    // build is newer than last time. That misleading post-update notice has been removed entirely.
    [Test]
    public void Run_NeverAnnouncesAnUpdateJustHappened()
    {
        var now = DateTime.UtcNow;
        var state = new VersionNotifier.State(now, "v2026.6.25.1");
        var (_, messages) = VersionNotifier.Run(state, new Version("2026.6.25.2"), now, () => null);
        Assert.That(messages.Any(m => m.Text.Contains("updated to cop", StringComparison.OrdinalIgnoreCase)), Is.False);
        Assert.That(messages.Any(m => m.Text.Contains("what's new", StringComparison.OrdinalIgnoreCase)), Is.False);
    }

    [Test]
    public void Run_Throttled_DoesNotFetch_WhenLastCheckRecent()
    {
        var now = DateTime.UtcNow;
        bool fetched = false;
        var state = new VersionNotifier.State(now.AddHours(-1), "v2026.6.22.3");
        var (newState, _) = VersionNotifier.Run(state, new Version("2026.6.22.3"), now,
            () => { fetched = true; return "v2026.6.99.9"; });

        Assert.That(fetched, Is.False, "must not hit the network within the throttle window");
        Assert.That(newState.LastCheck, Is.EqualTo(now.AddHours(-1)), "lastCheck unchanged when throttled");
    }

    [Test]
    public void Run_Stale_Fetches_UpdatesLatestTagAndLastCheck()
    {
        var now = DateTime.UtcNow;
        var state = new VersionNotifier.State(now.AddHours(-48), "v2026.6.22.3");
        var (newState, messages) = VersionNotifier.Run(state, new Version("2026.6.22.3"), now,
            () => "v2026.6.99.9");

        Assert.That(newState.LastCheck, Is.EqualTo(now));
        Assert.That(newState.LatestTag, Is.EqualTo("v2026.6.99.9"));
        Assert.That(messages.Any(m => m.Color == ConsoleColor.Yellow && m.Text.Contains("2026.6.99.9")), Is.True);
    }

    // ── State I/O ────────────────────────────────────────────────────

    [Test]
    public void State_RoundTripsThroughFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "cop-state-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var when = new DateTime(2026, 6, 22, 10, 0, 0, DateTimeKind.Utc);
            VersionNotifier.WriteState(path, new VersionNotifier.State(when, "v2026.6.22.9"));
            var read = VersionNotifier.ReadState(path);

            Assert.That(read.LatestTag, Is.EqualTo("v2026.6.22.9"));
            Assert.That(read.LastCheck, Is.EqualTo(when));
        }
        finally { File.Delete(path); }
    }
}
